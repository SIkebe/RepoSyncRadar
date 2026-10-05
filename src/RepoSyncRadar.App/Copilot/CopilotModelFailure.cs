using GitHub.Copilot;
using Microsoft.Extensions.Localization;

namespace RepoSyncRadar.App.Copilot;

public enum CopilotModelFailureKind
{
    InvalidRequest,
    InputTooLarge,
    RateLimited,
    RequestRejected,
    ServerError,
    TransportError,
}

public sealed class CopilotModelFailureException(CopilotModelFailureKind kind, Exception innerException)
    : Exception($"Copilot model operation failed: {kind}.", innerException)
{
    public CopilotModelFailureKind Kind { get; } = kind;
}

internal sealed class CopilotModelFailureCollector
{
    private readonly object _gate = new();
    private CopilotModelFailureKind? _latest;
    private CopilotModelFailureKind? _failed;

    internal void Observe(SessionEvent evt)
    {
        if (!string.IsNullOrEmpty(evt.AgentId))
        {
            return;
        }

        lock (_gate)
        {
            if (evt is ModelCallFinalResultEvent final)
            {
                _latest = final.Data.Result.Value switch
                {
                    "http_400" => CopilotModelFailureKind.InvalidRequest,
                    "http_413" => CopilotModelFailureKind.InputTooLarge,
                    "http_429" => CopilotModelFailureKind.RateLimited,
                    "http_4xx" => CopilotModelFailureKind.RequestRejected,
                    "http_5xx" => CopilotModelFailureKind.ServerError,
                    "transport_error" => CopilotModelFailureKind.TransportError,
                    _ => null,
                };
            }
            else if (evt is SessionErrorEvent error)
            {
                _failed = error.Data.ErrorType == "query" ? _latest : null;
                _latest = null;
            }
            else if (evt is ModelCallStartEvent or AssistantTurnStartEvent or ToolExecutionStartEvent)
            {
                _latest = null;
            }
        }
    }

    internal CopilotModelFailureException? Classify(Exception error)
    {
        lock (_gate)
        {
            return error is not OperationCanceledException and not TimeoutException && _failed is { } kind
                ? new CopilotModelFailureException(kind, error)
                : null;
        }
    }
}

internal static class CopilotFailureMessage
{
    internal static string? TryFormat(Exception error, IStringLocalizer<SharedResource> localizer)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is CopilotModelFailureException failure)
            {
                return localizer[$"Copilot.Failure.{failure.Kind}"];
            }
        }
        return null;
    }
}
