using Microsoft.Extensions.Logging;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
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
    internal const int ParallelTriageSessionCount = 2;

    /// <summary>The prompt body appended to the SDK system message (kept in code so unit tests can grep for the marker).</summary>
    internal const string TriagePrompt = """
        # Morning Triage

            Goal:
            最新の `github/docs` Repo sync PR 由来の未確認コミットを、ユーザーが朝のレビューで素早く「注目 / 保留 / 見送り候補」に判断できる状態へ整理する。

            Success criteria:
            - `radar_list_commits` を `status="Unseen"`, `limit=50` で呼び、まだスコアリングされていない未確認コミット一覧を取得する。
            - 各コミットについて `radar_score_commit` でスコア・カテゴリ・読者・要約・理由・詳細分析を保存する。
            - 0.44 以下の低スコアは `radar_score_commit` 保存時に自動で見送り候補へ分類される。0.45 以上の注目 / 保留判断はユーザーが一覧を見て行う。
            - `Rejected` / `Archived` / `Later` / `Adopted` などのレビュー状態はユーザーの最終判断で保存する。ただし、低スコアと登録済み Ignore ルールによる自動見送りは尊重する。
            - 既に確立されたユーザー設定 (Ignore / Boost) を尊重し、自動見送り済みの無視対象はスキップする。

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
            - `WhyJa`: 1 文、100 文字以内。スコア帯 (例: 0.70-0.84)・根拠・読者影響・レビュー緊急度を結び付けて、「なぜその点数か」を判断向けに書く。
            - `DetailsJa`: 次のラベルをこの順序で含める。各ラベルは 1 行、最大 90 文字程度。
              - `変更内容`: 変更の実体。ファイル名だけでなく、機能/API/設定/読者影響を書く。
              - `根拠`: 差分またはレンダリング済み本文で確認した、加点/減点に効いた事実。URL やパスは必要最小限で含める。
              - `影響`: 影響を受ける読者と、そのスコア帯にした理由。
              - `確認観点`: ユーザーが次に見るべき具体的な確認点。未確認推測はここだけに書く。

            Style:
            - 出力はすべて日本語。
            - GitHub の product scope としての `Organization` / `Enterprise` は英語のまま書き、どちらも `組織` と訳さない。
            - 段落を長くしない。
            - 同じ内容を `SummaryJa` / `WhyJa` / `DetailsJa` で繰り返さない。
            - 「新しい」「重要」だけで済ませず、何が誰にどう効き、なぜそのスコア帯なのかを書く。
            - 不明な場合は断定せず、`確認観点` に回す。

            Processing order:
            - 処理速度のため、最大 10 件までのコミットは `radar_get_diff` で先読みしてよい。
            - ただし 11 件以上の `radar_get_diff` をまとめて並列に呼び出さない。大きなバッチで差分取得を先行させない。
            - 採点できたコミットから `radar_score_commit` をすぐ呼び、1 件ずつ保存する。複数件の採点結果をためてからまとめて保存しない。
            - これにより分析はある程度まとめて進めつつ、進捗 UI とコミット一覧のスコアは 1 件単位で更新される。

            Stop rules:
            - 全件を処理したら短い完了報告だけ返す。
            - ツール呼び出しに必要な最小限の説明以外は返さない。
        """;

    private readonly ICommitIngestionService _ingestion;
    private readonly ICopilotSessionFactory _sessionFactory;
    private readonly TriageScoringProgressTracker _scoringProgress;
    private readonly ITriagePreflightSummaryBuilder? _preflightBuilder;
    private readonly IRadarRepository? _repository;
    private readonly IReviewBroadcaster? _reviewBroadcaster;
    private readonly ILogger<MorningTriageSession> _logger;

    public MorningTriageSession(
        ICommitIngestionService ingestion,
        ICopilotSessionFactory sessionFactory,
        ILogger<MorningTriageSession> logger)
        : this(ingestion, sessionFactory, new TriageScoringProgressTracker(), repository: null, reviewBroadcaster: null, logger)
    {
    }

    public MorningTriageSession(
        ICommitIngestionService ingestion,
        ICopilotSessionFactory sessionFactory,
        TriageScoringProgressTracker scoringProgress,
        ILogger<MorningTriageSession> logger)
        : this(ingestion, sessionFactory, scoringProgress, repository: null, reviewBroadcaster: null, logger)
    {
    }

    public MorningTriageSession(
        ICommitIngestionService ingestion,
        ICopilotSessionFactory sessionFactory,
        TriageScoringProgressTracker scoringProgress,
        IReviewBroadcaster? reviewBroadcaster,
        ILogger<MorningTriageSession> logger)
        : this(ingestion, sessionFactory, scoringProgress, repository: null, reviewBroadcaster, logger)
    {
    }

    public MorningTriageSession(
        ICommitIngestionService ingestion,
        ICopilotSessionFactory sessionFactory,
        TriageScoringProgressTracker scoringProgress,
        IRadarRepository? repository,
        IReviewBroadcaster? reviewBroadcaster,
        ILogger<MorningTriageSession> logger,
        ITriagePreflightSummaryBuilder? preflightBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(scoringProgress);
        ArgumentNullException.ThrowIfNull(logger);

        _ingestion = ingestion;
        _sessionFactory = sessionFactory;
        _scoringProgress = scoringProgress;
        _preflightBuilder = preflightBuilder;
        _repository = repository;
        _reviewBroadcaster = reviewBroadcaster;
        _logger = logger;
    }

    public async Task<TriagePreflightSummary> BuildPreflightAsync(
        bool includeGitHubEstimate,
        CancellationToken cancellationToken = default)
    {
        if (_preflightBuilder is null)
        {
            throw new InvalidOperationException("Morning Triage preflight is not configured.");
        }

        return await _preflightBuilder.BuildAsync(includeGitHubEstimate, cancellationToken).ConfigureAwait(false);
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

        var scoringTargets = await LoadScoringTargetsAsync(cancellationToken).ConfigureAwait(false);
        if (scoringTargets is { Count: 0 })
        {
            progress?.Report("今回の未スコア未確認コミットはありません。画面を更新しています…");
            return report with { RemainingUnscoredCommitCount = 0 };
        }

        progress?.Report("Copilot セッションを準備しています…");
        using var scoringScope = _scoringProgress.Begin(progress);
        if (scoringTargets is { Count: >= 2 })
        {
            await RunParallelScoringAsync(scoringTargets, progress, cancellationToken).ConfigureAwait(false);
            return await AddRemainingUnscoredCountAsync(report, cancellationToken).ConfigureAwait(false);
        }

        var session = await _sessionFactory.CreateSessionAsync(SessionPurpose.Triage, cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            try
            {
                LogSending(_logger, session.SessionId);
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

        return await AddRemainingUnscoredCountAsync(report, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Commit>?> LoadScoringTargetsAsync(CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return null;
        }

        return await _repository.QueryCommitsAsync(
            new CommitQueryFilter
            {
                Status = ReviewStatus.Unseen,
                Limit = TriagePreflightSummaryBuilder.ScoringTargetLimit,
                UnscoredOnly = true,
                OldestFirst = true,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IngestionReport> AddRemainingUnscoredCountAsync(
        IngestionReport report,
        CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return report;
        }

        var remaining = await _repository.QueryCommitsAsync(
            new CommitQueryFilter
            {
                Status = ReviewStatus.Unseen,
                UnscoredOnly = true,
            },
            cancellationToken).ConfigureAwait(false);
        return report with { RemainingUnscoredCommitCount = remaining.Count };
    }

    private async Task RunParallelScoringAsync(
        IReadOnlyList<Commit> commits,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        _scoringProgress.ReportCommitList(commits.Select(static commit => commit.Sha).ToArray());
        var shards = SplitIntoShards(commits, ParallelTriageSessionCount);
        var sessions = new List<ICopilotSession>(shards.Count);
        try
        {
            foreach (var _ in shards)
            {
                var session = await _sessionFactory.CreateSessionAsync(SessionPurpose.Triage, cancellationToken).ConfigureAwait(false);
                sessions.Add(session);
                LogSending(_logger, session.SessionId);
            }

            progress?.Report($"Copilot が {shards.Count} セッションで未確認コミットを並列スコアリングしています…");
            var tasks = sessions
                .Zip(shards, static (session, shard) => (session, shard))
                .Select(pair => pair.session.SendAsync(BuildShardPrompt(pair.shard), TriageSendTimeout, cancellationToken));
            _ = await Task.WhenAll(tasks).ConfigureAwait(false);
            progress?.Report("Triage が完了しました。画面を更新しています…");
            foreach (var session in sessions)
            {
                LogFinished(_logger, session.SessionId);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var session in sessions)
            {
                LogAborting(_logger, session.SessionId);
            }
            await AbortAllAsync(sessions).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await AbortAllAsync(sessions).ConfigureAwait(false);
            throw;
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static List<IReadOnlyList<Commit>> SplitIntoShards(IReadOnlyList<Commit> commits, int shardCount)
    {
        var shards = Enumerable.Range(0, Math.Min(shardCount, commits.Count))
            .Select(static _ => new List<Commit>())
            .ToList();
        for (var i = 0; i < commits.Count; i++)
        {
            shards[i % shards.Count].Add(commits[i]);
        }

        return shards.Select(static shard => (IReadOnlyList<Commit>)shard).ToList();
    }

    private static string BuildShardPrompt(IReadOnlyList<Commit> commits)
    {
        var items = string.Join("\n", commits.Select(static commit =>
            $"- {commit.Sha} PR #{commit.PrNumber}: {FirstLine(commit.Message)}\n  Files: {string.Join(", ", commit.Files.Select(static file => file.Path))}"));

        return $$"""
            # Morning Triage shard

            このセッションは Morning Triage の分割処理です。以下の SHA だけを採点してください。他の SHA は処理しないでください。

            {{items}}

            必須手順:
            - `radar_list_commits` は呼ばない。対象 SHA はこのプロンプト内の一覧だけです。
            - 各 SHA について `radar_get_diff` を呼び、差分を確認する。
            - user-facing な変更、0.70 以上になりそうな変更、または差分だけで判断できない変更のみ `radar_resolve_url` / `radar_fetch_rendered` を使う。
            - 各 SHA について必ず `radar_score_commit` を 1 回呼び、スコア・カテゴリ・読者・要約・理由・詳細分析を保存する。
            - 0.44 以下の低スコアは `radar_score_commit` 保存時に自動で見送り候補へ分類される。0.45 以上の注目 / 保留判断はユーザーが一覧を見て行う。
            - 登録済み Ignore ルールによる自動見送りは尊重する。
            - 全件を処理したら短く完了報告する。

            出力要件:
            - `SummaryJa`: 1 文、60 文字以内。
            - `WhyJa`: 1 文、100 文字以内。スコア帯・根拠・読者影響・レビュー緊急度を結び付けて、なぜその点数かを書く。
            - `DetailsJa`: `変更内容` / `根拠` / `影響` / `確認観点` をこの順序で含める。
            - 根拠に書けるのは差分またはレンダリング済み本文で確認した事実だけ。
            - GitHub の product scope としての `Organization` / `Enterprise` は英語のまま書き、どちらも `組織` と訳さない。
            """;
    }

    private static string FirstLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;

    private static async Task AbortAllAsync(IReadOnlyList<ICopilotSession> sessions)
    {
        foreach (var session in sessions)
        {
            try
            {
                await session.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
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
