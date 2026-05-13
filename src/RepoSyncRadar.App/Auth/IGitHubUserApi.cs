namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Thin port for "look up the GitHub login that owns the supplied access token".
/// Kept as a one-method seam so <see cref="GitHubAuthSession"/> can unit-test the
/// caching / fallback paths without booting Octokit + an HTTP stack.
/// </summary>
public interface IGitHubUserApi
{
    /// <summary>
    /// Returns the authenticated user's <c>login</c> handle (e.g. <c>octocat</c>)
    /// for the supplied OAuth access token, or <c>null</c> when the API call fails
    /// or the token has no associated user (which would itself be an auth bug).
    /// </summary>
    Task<string?> GetCurrentLoginAsync(string accessToken, CancellationToken cancellationToken);
}
