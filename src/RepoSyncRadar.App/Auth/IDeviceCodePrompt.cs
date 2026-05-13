namespace RepoSyncRadar.App.Auth;

/// <summary>
/// UI surface for displaying GitHub's device flow user code while the
/// <see cref="GitHubAccessTokenProvider"/> polls for the access token in the
/// background. Implementations must be safe to call from non-UI threads.
/// </summary>
public interface IDeviceCodePrompt : IAsyncDisposable
{
    /// <summary>
    /// Displays <paramref name="challenge"/> to the user and returns once the prompt
    /// is on-screen. The prompt remains visible until <see cref="CloseAsync"/> is
    /// called.
    /// </summary>
    Task DisplayAsync(DeviceCodeChallenge challenge, CancellationToken cancellationToken);

    /// <summary>Closes the prompt (no-op when it is already closed).</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}
