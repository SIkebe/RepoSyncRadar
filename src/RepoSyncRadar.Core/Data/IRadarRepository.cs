namespace RepoSyncRadar.Core.Data;

/// <summary>
/// Thin persistence facade that the integration layer (<c>DocsGitHubClient</c>) consults to
/// avoid re-ingesting commits that are already in the local store.
/// </summary>
/// <remarks>
/// Only the minimum surface needed by <c>Step 6</c> is exposed here. The full repository
/// (write operations, scoring updates, etc.) lands in <c>Step 7</c>.
/// </remarks>
public interface IRadarRepository
{
    /// <summary>
    /// Returns the set of commit SHAs already persisted in <c>radar.db</c>. The returned set is
    /// expected to support <see cref="ISet{T}.Contains"/> in O(1) so the caller can filter a
    /// freshly fetched commit list cheaply.
    /// </summary>
    Task<IReadOnlySet<string>> GetKnownShasAsync(CancellationToken cancellationToken = default);
}
