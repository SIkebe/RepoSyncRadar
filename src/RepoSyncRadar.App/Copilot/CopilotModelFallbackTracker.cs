namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Publishes the process-wide Copilot model fallback so the UI can tell users when
/// sessions no longer run on the configured <c>Copilot:DefaultModel</c>.
/// </summary>
public interface ICopilotModelFallbackTracker
{
    event Action? Changed;

    CopilotModelFallback? Current { get; }

    void RecordFallback(SessionPurpose purpose, string configuredModel, string fallbackModel);
}

public sealed class CopilotModelFallbackTracker : ICopilotModelFallbackTracker
{
    private readonly TimeProvider _timeProvider;
    private CopilotModelFallback? _current;

    public CopilotModelFallbackTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action? Changed;

    public CopilotModelFallback? Current => Volatile.Read(ref _current);

    public void RecordFallback(SessionPurpose purpose, string configuredModel, string fallbackModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackModel);

        Volatile.Write(
            ref _current,
            new CopilotModelFallback(_timeProvider.GetUtcNow(), purpose, configuredModel, fallbackModel));
        Changed?.Invoke();
    }
}

public sealed record CopilotModelFallback(
    DateTimeOffset OccurredAt,
    SessionPurpose Purpose,
    string ConfiguredModel,
    string FallbackModel);
