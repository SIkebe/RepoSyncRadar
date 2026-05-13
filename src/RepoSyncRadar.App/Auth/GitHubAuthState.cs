namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Non-destructive snapshot of the GitHub auth state for the UI. Distinct from
/// <see cref="Core.Auth.IGitHubAccessTokenProvider"/> in that <em>checking the state
/// here never triggers a device-flow round-trip</em>; it only reads the local store
/// and the configured <c>Copilot:OAuthClientId</c>.
/// </summary>
public enum GitHubAuthState
{
    /// <summary>
    /// <c>Copilot:OAuthClientId</c> is missing/whitespace. Neither sign-in nor Sync
    /// can proceed until the user registers a GitHub OAuth App and updates the
    /// configuration. The UI should surface a clear "setup required" affordance.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// <c>OAuthClientId</c> is configured but no valid token is cached/stored.
    /// The UI should offer a "Sign in" button that drives the device flow.
    /// </summary>
    NotSignedIn,

    /// <summary>
    /// A non-expired token is available locally. The UI can enable Sync and Copilot
    /// actions and offer a "Sign out" affordance.
    /// </summary>
    SignedIn,
}
