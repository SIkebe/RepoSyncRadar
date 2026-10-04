using System.Text.Json.Serialization;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using RepoSyncRadar.Core.Services;

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
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _modelDiagnosticsReliable = true;

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
        var assistant = await SendWithDiagnosticsAsync(
            () => _session.SendAndWaitAsync(prompt, timeout, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await RefreshUsageMetricsAsync(cancellationToken).ConfigureAwait(false);
        return assistant?.Data?.Content ?? string.Empty;
    }

#pragma warning disable GHCP001 // Typed structured output is experimental in the installed SDK.
    public async Task<DraftBundle> SendStructuredDraftAsync(
        string prompt,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var draft = await SendWithDiagnosticsAsync(
            () => _session.SendAndWaitAsync<StructuredDraft>(
                prompt, timeout: timeout, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await RefreshUsageMetricsAsync(cancellationToken).ConfigureAwait(false);
        return CreateDraftBundle(draft.Explanation, draft.Twitter, draft.Customer);
    }
#pragma warning restore GHCP001

    private async Task<T> SendWithDiagnosticsAsync<T>(Func<Task<T>> send, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var collector = new CopilotModelFailureCollector();
            using var subscription = _session.On<SessionEvent>(collector.Observe);
            try
            {
                return await send().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var reliable = _modelDiagnosticsReliable;
                // A failed wait may leave runtime work in flight; its late events cannot identify a subsequent send.
                _modelDiagnosticsReliable = false;
                if (reliable && !cancellationToken.IsCancellationRequested
                    && collector.Classify(ex) is { } classified)
                {
                    throw classified;
                }
                throw;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    internal static DraftBundle CreateDraftBundle(string? explanation, string? twitter, string? customer)
    {
        if (explanation is null)
        {
            throw new InvalidOperationException("Copilot returned a structured draft without explanation.");
        }
        if (twitter is null)
        {
            throw new InvalidOperationException("Copilot returned a structured draft without twitter.");
        }
        if (customer is null)
        {
            throw new InvalidOperationException("Copilot returned a structured draft without customer.");
        }

        return new DraftBundle(twitter, string.Empty, customer, explanation);
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

    // Non-nullable fields keep the inferred schema strict; the mapper rejects malformed provider output.
    private sealed record StructuredDraft(
        [property: JsonPropertyName("explanation")] string Explanation,
        [property: JsonPropertyName("twitter")] string Twitter,
        [property: JsonPropertyName("customer")] string Customer);
}
