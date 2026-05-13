using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.Core.Data;

/// <summary>
/// Persistence facade for the local <c>radar.db</c> store. Used by the integration layer
/// (<c>DocsGitHubClient</c>) to skip already-ingested commits, by the ingestion pipeline
/// (<c>CommitIngestionService</c>) for the idempotent upsert flow, and by the review UI
/// to record adoption decisions.
/// </summary>
public interface IRadarRepository
{
    /// <summary>
    /// Returns every commit SHA already persisted in <c>radar.db</c>. The returned set is
    /// expected to support <see cref="ISet{T}.Contains"/> in O(1) so the caller can filter a
    /// freshly fetched commit list cheaply.
    /// </summary>
    Task<IReadOnlySet<string>> GetKnownShasAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the subset of <paramref name="candidateShas"/> that are already persisted in
    /// <c>radar.db</c>. Use this in preference to <see cref="GetKnownShasAsync(CancellationToken)"/>
    /// when only a small candidate set needs filtering, so the database scan stays cheap.
    /// </summary>
    Task<IReadOnlySet<string>> GetKnownShasAsync(
        IEnumerable<string> candidateShas,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the supplied <paramref name="commits"/> using "insert-only" semantics: commits
    /// whose <see cref="Commit.Sha"/> is already in the store are left untouched (including
    /// their <see cref="Commit.FetchedAt"/>), so this method is safe to call repeatedly with
    /// overlapping batches. <see cref="Commit.Files"/> are cascade-persisted for newly inserted
    /// rows only.
    /// </summary>
    /// <returns>The SHAs that were actually inserted, in input order.</returns>
    Task<IReadOnlyList<string>> UpsertCommitsAsync(
        IEnumerable<Commit> commits,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the <see cref="Review"/> row for the given commit. When no
    /// <see cref="Review"/> exists, a new one is inserted with the supplied <paramref name="status"/>
    /// and <paramref name="reason"/>. When a row already exists, its <see cref="Review.Status"/>,
    /// <see cref="Review.Reason"/>, and <see cref="Review.ReviewedAt"/> are overwritten.
    /// </summary>
    Task SetReviewAsync(
        string sha,
        ReviewStatus status,
        string? reason,
        CancellationToken cancellationToken = default);
}
