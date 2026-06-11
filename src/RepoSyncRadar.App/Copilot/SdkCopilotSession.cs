using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Production <see cref="ICopilotSession"/> that adapts the real Copilot SDK session.
/// Owns the underlying <see cref="CopilotSession"/> handle and forwards lifecycle calls.
/// </summary>
internal sealed partial class SdkCopilotSession : ICopilotSession
{
    private readonly CopilotSession _session;
    private readonly SessionPurpose _purpose;
    private readonly ILogger _logger;
    private readonly ICopilotUsageTracker? _usageTracker;
    private readonly IDisposable? _usageSubscription;

    public SdkCopilotSession(
        CopilotSession session,
        SessionPurpose purpose,
        ILogger logger,
        ICopilotUsageTracker? usageTracker)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);
        _session = session;
        _purpose = purpose;
        _logger = logger;
        _usageTracker = usageTracker;
        if (usageTracker is not null)
        {
            _usageSubscription = session.On<AssistantUsageEvent>(usage =>
            {
                usageTracker.Record(CopilotUsageTracker.FromAssistantUsage(usage, purpose, session.SessionId));
            });
        }
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
        var assistant = await _session.SendAndWaitAsync(prompt, timeout, cancellationToken).ConfigureAwait(false);
        await RefreshUsageMetricsAsync(cancellationToken).ConfigureAwait(false);
        return assistant?.Data?.Content ?? string.Empty;
    }

    private async Task RefreshUsageMetricsAsync(CancellationToken cancellationToken)
    {
        if (_usageTracker is null)
        {
            return;
        }

        try
        {
            var metrics = await _session.Rpc.Usage.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
            _usageTracker.RecordSessionMetrics(CopilotUsageTracker.FromSessionMetrics(metrics, _purpose, _session.SessionId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUsageMetricsRefreshFailed(_logger, ex, _session.SessionId);
        }
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
        => _session.AbortAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _usageSubscription?.Dispose();
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Could not refresh Copilot SDK usage metrics for session {SessionId}.")]
    private static partial void LogUsageMetricsRefreshFailed(ILogger logger, Exception ex, string sessionId);
}
