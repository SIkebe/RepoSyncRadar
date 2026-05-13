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

    /// <summary>
    /// Returns commits matching <paramref name="filter"/>, eagerly loading <see cref="Commit.Files"/>
    /// and <see cref="Commit.Review"/>, ordered by <see cref="Commit.AuthoredAt"/> descending.
    /// A commit without a <see cref="Review"/> row is treated as <see cref="ReviewStatus.Unseen"/>.
    /// </summary>
    Task<IReadOnlyList<Commit>> QueryCommitsAsync(
        CommitQueryFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a count per <see cref="ReviewStatus"/> across all persisted commits. Commits
    /// without a <see cref="Review"/> row count toward <see cref="ReviewStatus.Unseen"/>. The
    /// returned dictionary is guaranteed to contain every <see cref="ReviewStatus"/> key (with
    /// <c>0</c> when no commits match) so the sidebar can render without null checks.
    /// </summary>
    Task<IReadOnlyDictionary<ReviewStatus, int>> GetReviewCountsAsync(
        CancellationToken cancellationToken = default);
}
