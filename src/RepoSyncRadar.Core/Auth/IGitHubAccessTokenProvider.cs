namespace RepoSyncRadar.Core.Auth;

/// <summary>
/// Single entry point for obtaining a valid GitHub user token. The same OAuth user
/// token is shared between the Copilot SDK (passed as <c>GitHubToken</c>) and the
/// Octokit-based <see cref="Services.GitHub.IDocsGitHubClient"/>, so callers only need
/// one credential surface.
/// </summary>
/// <remarks>
/// The interface lives in Core so that <see cref="Services.GitHub.DocsGitHubClient"/>
/// (which has no dependency on WPF or DPAPI) can consume it. The concrete
/// implementation lives in the App layer because it needs DPAPI persistence, the
/// device-flow UI, and the WPF dispatcher.
/// </remarks>
public interface IGitHubAccessTokenProvider
{
    /// <summary>
    /// Returns a valid GitHub OAuth user token, signing the user in via device flow
    /// when needed. May display UI before returning.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no token is cached/stored and the device flow cannot run because
    /// <c>Copilot:OAuthClientId</c> is not configured.
    /// </exception>
    /// <exception cref="DeviceFlowFailedException">
    /// Thrown when the device flow itself fails (user denied, timeout, network).
    /// </exception>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>Clears the in-memory cache and the persisted token.</summary>
    Task SignOutAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when GitHub returns a terminal error during the device flow
/// (<c>access_denied</c>, <c>expired_token</c>, <c>incorrect_client_credentials</c>,
/// network failure, etc.).
/// </summary>
public sealed class DeviceFlowFailedException : Exception
{
    public DeviceFlowFailedException(string message) : base(message) { }
    public DeviceFlowFailedException(string message, Exception inner) : base(message, inner) { }
}
