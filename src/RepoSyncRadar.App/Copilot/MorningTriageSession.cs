using Microsoft.Extensions.Logging;
using RepoSyncRadar.Core.Services;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Orchestrates the Morning Triage flow (DESIGN.md §6, IMPLEMENTATION_PLAN.md §Step 15):
/// <list type="number">
///   <item><description>Pulls the latest unseen commits via <see cref="ICommitIngestionService"/>.</description></item>
///   <item><description>Opens a <see cref="SessionPurpose.MorningTriage"/> Copilot session.</description></item>
///   <item><description>Lets Copilot iterate over <c>radar_list_commits</c> → <c>radar_get_diff</c> → <c>radar_score_commit</c> until idle.</description></item>
/// </list>
/// Cancellation aborts the in-flight session before the exception propagates.
/// </summary>
public sealed partial class MorningTriageSession
{
    /// <summary>The prompt body appended to the SDK system message (kept in code so unit tests can grep for the marker).</summary>
    internal const string TriagePrompt = """
        # Morning Triage
        最新の `github/docs` Repo sync PR 由来のコミットを処理してください。

        手順:
        1. `radar_list_commits` を `status="Unseen"`, `limit=50` で呼び、未読コミット一覧を取得する。
        2. 各コミットについて必要に応じて `radar_get_diff` で差分を確認し、`radar_resolve_url` / `radar_fetch_rendered` で出典ページを確認する。
        3. 影響範囲・新規性・読者層を判断し、`radar_score_commit` でスコア・カテゴリ・読者・要約・理由を保存する。
        4. スコア上位 5 件を Must read として未読のまま残し、次点の Skim 候補は `radar_save_review` で `Seen`、明らかに不要な Archive 候補は `Rejected` として保存する。
        5. 既に確立されたユーザー設定 (Ignore / Boost) を尊重し、無視対象はスキップする。
        6. 全件を処理し終えたら短い完了報告を返す。

        出力はすべて日本語で、必要なツール呼び出しを最後まで実行してください。
        """;

    private readonly ICommitIngestionService _ingestion;
    private readonly ICopilotSessionFactory _sessionFactory;
    private readonly ILogger<MorningTriageSession> _logger;

    public MorningTriageSession(
        ICommitIngestionService ingestion,
        ICopilotSessionFactory sessionFactory,
        ILogger<MorningTriageSession> logger)
    {
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _ingestion = ingestion;
        _sessionFactory = sessionFactory;
        _logger = logger;
    }

    /// <summary>Runs the full Morning Triage workflow. Returns the ingestion stats for status display.</summary>
    public async Task<IngestionReport> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogStarting(_logger);

        var report = await _ingestion.IngestAsync(cancellationToken).ConfigureAwait(false);
        LogIngested(_logger, report.Total, report.Inserted, report.Skipped);

        var session = await _sessionFactory.CreateSessionAsync(SessionPurpose.Triage, cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            try
            {
                LogSending(_logger, session.SessionId);
                _ = await session.SendAsync(TriagePrompt, cancellationToken).ConfigureAwait(false);
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
}
