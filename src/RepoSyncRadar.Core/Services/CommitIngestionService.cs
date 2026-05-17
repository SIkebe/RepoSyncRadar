using RepoSyncRadar.Core.Data;

namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Default implementation of <see cref="ICommitIngestionService"/>. The pipeline is intentionally
/// linear so it stays easy to reason about: fetch → filter known → enrich with files → upsert.
/// </summary>
public sealed class CommitIngestionService : ICommitIngestionService
{
    private readonly IDocsGitHubClient _docs;
    private readonly IRadarRepository _repository;

    public CommitIngestionService(IDocsGitHubClient docs, IRadarRepository repository)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(repository);
        _docs = docs;
        _repository = repository;
    }

    public async Task<IngestionReport> IngestAsync(CancellationToken cancellationToken = default)
        => await IngestAsync(progress: null, cancellationToken).ConfigureAwait(false);

    public async Task<IngestionReport> IngestAsync(
        IProgress<CommitIngestionProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = await _docs.FetchUnseenCommitsAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            progress?.Report(new CommitIngestionProgress(Total: 0, Processed: 0, Inserted: 0, Skipped: 0, InsertedSha: null));
            return new IngestionReport(Total: 0, Inserted: 0, Skipped: 0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var candidateShas = candidates.Select(c => c.Sha).ToList();
        var known = await _repository.GetKnownShasAsync(candidateShas, cancellationToken).ConfigureAwait(false);

        var insertedShas = new List<string>(candidates.Count);
        var skipped = 0;
        var processed = 0;
        foreach (var commit in candidates)
        {
            if (known.Contains(commit.Sha))
            {
                skipped++;
                processed++;
                ReportProgress(progress, candidates.Count, processed, insertedShas.Count, skipped, insertedSha: null);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var files = await _docs.GetCommitFilesAsync(commit.Sha, cancellationToken).ConfigureAwait(false);
            commit.Files.Clear();
            commit.Files.AddRange(files);
            cancellationToken.ThrowIfCancellationRequested();

            var inserted = await _repository.UpsertCommitsAsync([commit], cancellationToken).ConfigureAwait(false);
            processed++;
            if (inserted.Count == 0)
            {
                skipped++;
                ReportProgress(progress, candidates.Count, processed, insertedShas.Count, skipped, insertedSha: null);
                continue;
            }

            insertedShas.AddRange(inserted);
            ReportProgress(progress, candidates.Count, processed, insertedShas.Count, skipped, inserted[0]);
        }

        return new IngestionReport(
            Total: candidates.Count,
            Inserted: insertedShas.Count,
            Skipped: candidates.Count - insertedShas.Count);
    }

    private static void ReportProgress(
        IProgress<CommitIngestionProgress>? progress,
        int total,
        int processed,
        int inserted,
        int skipped,
        string? insertedSha)
    {
        progress?.Report(new CommitIngestionProgress(total, processed, inserted, skipped, insertedSha));
    }
}
