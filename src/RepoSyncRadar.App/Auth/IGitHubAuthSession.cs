namespace RepoSyncRadar.App.Auth;

/// <summary>
/// UI-facing facade around <see cref="Core.Auth.IGitHubAccessTokenProvider"/> +
/// <see cref="IGitHubTokenStore"/>. Adds a non-destructive <see cref="GetStateAsync"/>
/// surface that the AppHeader uses to choose between "未設定 / 未サインイン /
/// サインイン済み" without ever firing the device flow as a side-effect.
/// </summary>
/// <remarks>
/// Sign-in and Sign-out are deliberate delegations to
/// <see cref="Core.Auth.IGitHubAccessTokenProvider"/> so the existing OAuth flow,
/// DPAPI persistence, and in-memory cache invalidation remain in one place.
/// </remarks>
public interface IGitHubAuthSession
{
    /// <summary>Returns the current auth state without contacting GitHub.</summary>
    Task<GitHubAuthState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Triggers the OAuth device flow (or reuses a cached/stored token) via
    /// <see cref="Core.Auth.IGitHubAccessTokenProvider.GetAccessTokenAsync"/>.
    /// Throws <see cref="InvalidOperationException"/> when the auth state is
    /// <see cref="GitHubAuthState.NotConfigured"/>.
    /// </summary>
    Task SignInAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Clears the in-memory cache and the persisted DPAPI token via
    /// <see cref="Core.Auth.IGitHubAccessTokenProvider.SignOutAsync"/>.
    /// </summary>
    Task SignOutAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the login handle (e.g. <c>octocat</c>) of the currently signed-in
    /// GitHub user, or <c>null</c> when the state is not <see cref="GitHubAuthState.SignedIn"/>
    /// or the lookup fails. Results are cached per access token so the AppHeader
    /// can call this on every refresh without spamming GitHub.
    /// </summary>
    Task<string?> GetCurrentLoginAsync(CancellationToken cancellationToken);
}
