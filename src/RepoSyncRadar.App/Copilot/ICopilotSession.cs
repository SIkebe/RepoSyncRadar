namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Thin abstraction over <see cref="GitHub.Copilot.CopilotSession"/> so the agent
/// orchestrators (Morning Triage and Draft generation) can be unit-tested
/// without the embedded Copilot CLI. The production implementation
/// (<see cref="SdkCopilotSession"/>) forwards calls to the real SDK.
/// </summary>
public interface ICopilotSession : IAsyncDisposable
{
    /// <summary>The Copilot SDK session id (correlation for audit + logs).</summary>
    string SessionId { get; }

    /// <summary>
    /// Sends a single user prompt and asynchronously waits for the agent loop (tool calls
    /// included) to finish. Returns the final assistant message text.
    /// </summary>
    Task<string> SendAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a single user prompt and waits for completion using the supplied idle timeout.
    /// </summary>
    Task<string> SendAsync(
        string prompt,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Aborts the current turn. Safe to call after the session has already ended.</summary>
    Task AbortAsync(CancellationToken cancellationToken = default);
}
