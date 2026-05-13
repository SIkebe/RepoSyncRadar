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
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = await _docs.FetchUnseenCommitsAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return new IngestionReport(Total: 0, Inserted: 0, Skipped: 0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var candidateShas = candidates.Select(c => c.Sha).ToList();
        var known = await _repository.GetKnownShasAsync(candidateShas, cancellationToken).ConfigureAwait(false);

        var freshCommits = new List<Models.Commit>(candidates.Count);
        foreach (var commit in candidates)
        {
            if (known.Contains(commit.Sha))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var files = await _docs.GetCommitFilesAsync(commit.Sha, cancellationToken).ConfigureAwait(false);
            commit.Files.Clear();
            commit.Files.AddRange(files);
            freshCommits.Add(commit);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var inserted = await _repository.UpsertCommitsAsync(freshCommits, cancellationToken).ConfigureAwait(false);

        return new IngestionReport(
            Total: candidates.Count,
            Inserted: inserted.Count,
            Skipped: candidates.Count - inserted.Count);
    }
}
