namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Incremental progress emitted while Repo sync commits are persisted into the local inbox.
/// </summary>
public sealed record CommitIngestionProgress(
    int Total,
    int Processed,
    int Inserted,
    int Skipped,
    string? InsertedSha);