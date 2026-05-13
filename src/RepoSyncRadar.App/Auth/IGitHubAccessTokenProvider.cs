namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Single entry point used by <c>CopilotSessionFactory</c> to obtain a valid GitHub
/// user token. Implementations are expected to cache the token in memory so repeated
/// calls during the same process are free.
/// </summary>
public interface IGitHubAccessTokenProvider
{
    /// <summary>
    /// Returns a valid GitHub OAuth user token, signing the user in via device flow
    /// when needed. May display UI (<see cref="IDeviceCodePrompt"/>) before returning.
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
