namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Orchestrates the idempotent ingestion pipeline: fetch unseen commits via
/// <see cref="IDocsGitHubClient"/>, filter out anything that is already persisted via
/// <see cref="Data.IRadarRepository"/>, populate per-commit file metadata lazily, and persist
/// newly discovered commits incrementally so the UI can refresh during triage.
/// </summary>
public interface ICommitIngestionService
{
    Task<IngestionReport> IngestAsync(CancellationToken cancellationToken = default);

    Task<IngestionReport> IngestAsync(
        IProgress<CommitIngestionProgress>? progress,
        CancellationToken cancellationToken = default);
}
