namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Outcome of a single <see cref="ICommitIngestionService.IngestAsync"/> run.
/// </summary>
/// <param name="Total">Number of commits returned by <see cref="IDocsGitHubClient.FetchUnseenCommitsAsync"/>.</param>
/// <param name="Inserted">Number of commits that were freshly persisted to <c>radar.db</c>.</param>
/// <param name="Skipped">Number of commits that were already present and therefore left untouched.</param>
public sealed record IngestionReport(int Total, int Inserted, int Skipped)
{
    /// <summary>
    /// Number of unscored, unreviewed commits remaining after Morning Triage. This is null when
    /// the report was produced by ingestion alone.
    /// </summary>
    public int? RemainingUnscoredCommitCount { get; init; }
}
