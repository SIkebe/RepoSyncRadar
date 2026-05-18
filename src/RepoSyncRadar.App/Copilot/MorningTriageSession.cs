using Microsoft.Extensions.Logging;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Services;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Orchestrates the Morning Triage flow (DESIGN.md §6, IMPLEMENTATION_PLAN.md §Step 15):
/// <list type="number">
///   <item><description>Pulls the latest unreviewed commits via <see cref="ICommitIngestionService"/>.</description></item>
///   <item><description>Opens a <see cref="SessionPurpose.MorningTriage"/> Copilot session.</description></item>
///   <item><description>Lets Copilot iterate over <c>radar_list_commits</c> → <c>radar_get_diff</c> → <c>radar_score_commit</c> until idle.</description></item>
/// </list>
/// Cancellation aborts the in-flight session before the exception propagates.
/// </summary>
public sealed partial class MorningTriageSession
{
    internal static readonly TimeSpan TriageSendTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The prompt body appended to the SDK system message (kept in code so unit tests can grep for the marker).</summary>
    internal const string TriagePrompt = """
        # Morning Triage

            Goal:
            最新の `github/docs` Repo sync PR 由来の未確認コミットを、ユーザーが朝のレビューで素早く「注目 / 保留 / 見送り候補」に判断できる状態へ整理する。

            Success criteria:
            - `radar_list_commits` を `status="Unseen"`, `limit=50` で呼び、まだスコアリングされていない未確認コミット一覧を取得する。
            - 各コミットについて `radar_score_commit` でスコア・カテゴリ・読者・要約・理由・詳細分析を保存する。
            - スコア上位 5 件と判断に迷う候補は未確認のまま残す。
            - 明らかに不要な候補だけ `radar_save_review` で `Rejected` として保存する。
            - `Rejected` は自動見送り候補を表す。`Archived` はユーザーが手動でアーカイブするときだけ使うため、Triage では使わない。
            - 既に確立されたユーザー設定 (Ignore / Boost) を尊重し、無視対象はスキップする。

            Evidence budget:
            - まず `radar_get_diff` で差分を確認する。
            - user-facing な変更、0.70 以上になりそうな変更、または差分だけで判断できない変更のみ `radar_resolve_url` / `radar_fetch_rendered` を使う。
            - 根拠に書けるのは差分またはレンダリング済み本文で確認した事実だけ。
            - 推測・未確認事項は `確認観点` に分ける。

            Scoring rubric:
            - 0.85-1.00: すぐ共有・確認すべき変更。新 API、破壊的変更、セキュリティ、非推奨、管理者/開発者の対応が必要な変更。
            - 0.70-0.84: 重要な機能追加、公開プレビュー、設定・運用・統合に影響する変更。
            - 0.45-0.69: 有用だが急ぎではない docs 追加、説明改善、対象者が限定的な変更。
            - 0.00-0.44: typo、内部整理、リンク修正、翻訳/表記調整、重複や低シグナルな更新。

            Category:
            次のいずれかを優先して使う: `feature-update`, `breaking-change`, `security`, `deprecation`, `api-change`, `admin-change`, `docs-maintenance`, `low-signal`。

            Audience:
            次のタグから最大 4 個: `developer`, `admin`, `customer`, `support`, `partner`, `internal`, `devrel`。

            Output requirements for `radar_score_commit`:
            - `SummaryJa`: 1 文、60 文字以内。「何が変わったか」を具体的に書く。
            - `WhyJa`: 1 文、80 文字以内。「なぜ見るべきか / 見送れるか」を判断向けに書く。
            - `DetailsJa`: 次のラベルをこの順序で含める。各ラベルは 1 行、最大 90 文字程度。
              - `変更内容`: 変更の実体。ファイル名だけでなく、機能/API/設定/読者影響を書く。
              - `根拠`: 差分またはレンダリング済み本文で確認した事実。URL やパスは必要最小限で含める。
              - `影響`: 影響を受ける読者と、レビュー/共有/対応が必要な理由。
              - `確認観点`: ユーザーが次に見るべき具体的な確認点。未確認推測はここだけに書く。

            Style:
            - 出力はすべて日本語。
            - 段落を長くしない。
            - 同じ内容を `SummaryJa` / `WhyJa` / `DetailsJa` で繰り返さない。
            - 「新しい」「重要」だけで済ませず、何が誰にどう効くかを書く。
            - 不明な場合は断定せず、`確認観点` に回す。

            Stop rules:
            - 全件を処理したら短い完了報告だけ返す。
            - ツール呼び出しに必要な最小限の説明以外は返さない。
        """;

    private readonly ICommitIngestionService _ingestion;
    private readonly ICopilotSessionFactory _sessionFactory;
    private readonly TriageScoringProgressTracker _scoringProgress;
    private readonly IReviewBroadcaster? _reviewBroadcaster;
    private readonly ILogger<MorningTriageSession> _logger;

    public MorningTriageSession(
        ICommitIngestionService ingestion,
        ICopilotSessionFactory sessionFactory,
        ILogger<MorningTriageSession> logger)
        : this(ingestion, sessionFactory, new TriageScoringProgressTracker(), reviewBroadcaster: null, logger)
    {
    }

    public MorningTriageSession(
        ICommitIngestionService ingestion,
        ICopilotSessionFactory sessionFactory,
        TriageScoringProgressTracker scoringProgress,
        ILogger<MorningTriageSession> logger)
        : this(ingestion, sessionFactory, scoringProgress, reviewBroadcaster: null, logger)
    {
    }

    public MorningTriageSession(
        ICommitIngestionService ingestion,
        ICopilotSessionFactory sessionFactory,
        TriageScoringProgressTracker scoringProgress,
        IReviewBroadcaster? reviewBroadcaster,
        ILogger<MorningTriageSession> logger)
    {
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(scoringProgress);
        ArgumentNullException.ThrowIfNull(logger);

        _ingestion = ingestion;
        _sessionFactory = sessionFactory;
        _scoringProgress = scoringProgress;
        _reviewBroadcaster = reviewBroadcaster;
        _logger = logger;
    }

    /// <summary>Runs the full Morning Triage workflow. Returns the ingestion stats for status display.</summary>
    public async Task<IngestionReport> RunAsync(CancellationToken cancellationToken = default)
        => await RunAsync(progress: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Runs the full Morning Triage workflow. Returns the ingestion stats for status display.</summary>
    public async Task<IngestionReport> RunAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogStarting(_logger);

        progress?.Report("Repo sync PR を取得しています…");
        var ingestionProgress = new TriageIngestionProgress(progress, _reviewBroadcaster);
        var report = await _ingestion.IngestAsync(ingestionProgress, cancellationToken).ConfigureAwait(false);
        LogIngested(_logger, report.Total, report.Inserted, report.Skipped);
        progress?.Report($"取り込み完了: 取得 {report.Total} / 新規 {report.Inserted} / スキップ {report.Skipped}");

        progress?.Report("Copilot セッションを準備しています…");
        var session = await _sessionFactory.CreateSessionAsync(SessionPurpose.Triage, cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            try
            {
                LogSending(_logger, session.SessionId);
                using var scoringScope = _scoringProgress.Begin(progress);
                progress?.Report("Copilot が未確認コミット一覧を取得し、スコアリングを開始しています…");
                _ = await session.SendAsync(TriagePrompt, TriageSendTimeout, cancellationToken).ConfigureAwait(false);
                progress?.Report("Triage が完了しました。画面を更新しています…");
                LogFinished(_logger, session.SessionId);
            }
            catch (OperationCanceledException)
            {
                LogAborting(_logger, session.SessionId);
                try
                {
                    await session.AbortAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception abortEx)
                {
                    LogAbortFailed(_logger, abortEx, session.SessionId);
                }
                throw;
            }
        }

        return report;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Morning triage starting.")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Ingestion complete: total={Total}, inserted={Inserted}, skipped={Skipped}.")]
    private static partial void LogIngested(ILogger logger, int total, int inserted, int skipped);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Sending triage prompt to session {SessionId}.")]
    private static partial void LogSending(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Morning triage finished cleanly (session={SessionId}).")]
    private static partial void LogFinished(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Cancellation requested; aborting session {SessionId}.")]
    private static partial void LogAborting(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "Session abort failed (session={SessionId}); the error is being swallowed.")]
    private static partial void LogAbortFailed(ILogger logger, Exception ex, string sessionId);

    private sealed class TriageIngestionProgress : IProgress<CommitIngestionProgress>
    {
        private readonly IProgress<string>? _progress;
        private readonly IReviewBroadcaster? _reviewBroadcaster;

        public TriageIngestionProgress(IProgress<string>? progress, IReviewBroadcaster? reviewBroadcaster)
        {
            _progress = progress;
            _reviewBroadcaster = reviewBroadcaster;
        }

        public void Report(CommitIngestionProgress value)
        {
            if (value.Total == 0)
            {
                _progress?.Report("Repo sync PR に新規未確認コミットはありません。");
                return;
            }

            if (value.InsertedSha is { Length: > 0 } sha)
            {
                _reviewBroadcaster?.Publish();
                _progress?.Report($"未確認コミットを取り込み中: 新規 {value.Inserted} / 取得 {value.Total} 件 ({ShortSha(sha)})");
            }
        }

        private static string ShortSha(string sha)
            => sha[..Math.Min(8, sha.Length)];
    }
}
