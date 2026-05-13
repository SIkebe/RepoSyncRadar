namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Performs GitHub's OAuth Device Flow against <c>github.com</c>. See
/// <see href="https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow"/>.
/// The interface is split into the two flow steps so the UI can render the user code
/// between them.
/// </summary>
public interface IGitHubDeviceFlowAuthenticator
{
    /// <summary>
    /// Requests a fresh device + user code pair from GitHub. The returned
    /// <see cref="DeviceCodeChallenge.VerificationUri"/> is what the user must open
    /// in a browser to type the <see cref="DeviceCodeChallenge.UserCode"/>.
    /// </summary>
    Task<DeviceCodeChallenge> RequestCodeAsync(
        string clientId,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Polls GitHub until the user authorizes the device, the code expires, or the
    /// user denies access. Honors <see cref="DeviceCodeChallenge.Interval"/> and the
    /// <c>slow_down</c> protocol error by extending the delay by 5 seconds.
    /// Throws <see cref="RepoSyncRadar.Core.Auth.DeviceFlowFailedException"/> on terminal errors.
    /// </summary>
    Task<StoredGitHubToken> PollForTokenAsync(
        string clientId,
        DeviceCodeChallenge challenge,
        CancellationToken cancellationToken);
}
