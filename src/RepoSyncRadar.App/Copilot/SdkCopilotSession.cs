using GitHub.Copilot.SDK;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Production <see cref="ICopilotSession"/> that adapts the real Copilot SDK session.
/// Owns the underlying <see cref="CopilotSession"/> handle and forwards lifecycle calls.
/// </summary>
internal sealed class SdkCopilotSession : ICopilotSession
{
    private readonly CopilotSession _session;

    public SdkCopilotSession(CopilotSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public string SessionId => _session.SessionId;

    public async Task<string> SendAsync(string prompt, CancellationToken cancellationToken = default)
        => await SendAsync(prompt, timeout: null, cancellationToken).ConfigureAwait(false);

    public async Task<string> SendAsync(
        string prompt,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var options = new MessageOptions { Prompt = prompt };
        var assistant = await _session.SendAndWaitAsync(options, timeout, cancellationToken).ConfigureAwait(false);
        return assistant?.ToString() ?? string.Empty;
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
        => _session.AbortAsync(cancellationToken);

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
