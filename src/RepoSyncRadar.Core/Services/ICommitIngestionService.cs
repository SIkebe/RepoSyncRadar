namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Orchestrates the idempotent ingestion pipeline: fetch unseen commits via
/// <see cref="IDocsGitHubClient"/>, filter out anything that is already persisted via
/// <see cref="Data.IRadarRepository"/>, populate per-commit file metadata lazily, and finally
/// persist the new commits in a single upsert.
/// </summary>
public interface ICommitIngestionService
{
    Task<IngestionReport> IngestAsync(CancellationToken cancellationToken = default);
}
