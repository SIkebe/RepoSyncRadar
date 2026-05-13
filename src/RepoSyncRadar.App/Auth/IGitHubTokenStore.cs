namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Persistence boundary for the GitHub user token. On Windows the default
/// implementation encrypts the file with DPAPI under the current Windows user;
/// tests substitute an in-memory variant.
/// </summary>
public interface IGitHubTokenStore
{
    /// <summary>
    /// Returns the stored token or <c>null</c> when no token is saved or the file is
    /// corrupt/undecryptable (e.g. user profile migrated to another machine). Callers
    /// should treat <c>null</c> as "user is signed out".
    /// </summary>
    Task<StoredGitHubToken?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(StoredGitHubToken token, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}
