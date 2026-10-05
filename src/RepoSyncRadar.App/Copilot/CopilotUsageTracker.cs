using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace RepoSyncRadar.App.Copilot;

public interface ICopilotUsageTracker
{
    event Action? Changed;

    CopilotUsageSnapshot GetSnapshot();

    void Record(CopilotUsageRecord record);

    long CaptureMetricsWatermark();

    void RecordSessionMetrics(CopilotSessionUsageMetrics metrics, long? watermark = null);

    void Reset();
}

public sealed class CopilotUsageTracker : ICopilotUsageTracker
{
    public const double NanoAiuPerAiCredit = 1_000_000_000d;

    private const int _maxRecentRecords = 50;
    private readonly object _gate = new();
    private readonly List<(long Sequence, CopilotUsageRecord Record)> _records = [];
    private readonly Dictionary<string, CopilotSessionUsageMetrics> _sessionMetrics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _metricsWatermarks = new(StringComparer.Ordinal);
    private long _sequence;
    private long _resetWatermark;

    public event Action? Changed;

    public CopilotUsageSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var uncoveredRecords = _records
                .Where(entry => !_metricsWatermarks.TryGetValue(entry.Record.SessionId, out var watermark)
                    || entry.Sequence > watermark)
                .Select(static entry => entry.Record).ToArray();
            var inputTokens = _sessionMetrics.Values.Sum(static metrics => metrics.InputTokens)
                + uncoveredRecords.Sum(static record => record.InputTokens);
            var outputTokens = _sessionMetrics.Values.Sum(static metrics => metrics.OutputTokens)
                + uncoveredRecords.Sum(static record => record.OutputTokens);
            var reasoningTokens = _sessionMetrics.Values.Sum(static metrics => metrics.ReasoningTokens)
                + uncoveredRecords.Sum(static record => record.ReasoningTokens);
            var cacheReadTokens = _sessionMetrics.Values.Sum(static metrics => metrics.CacheReadTokens)
                + uncoveredRecords.Sum(static record => record.CacheReadTokens);
            var cacheWriteTokens = _sessionMetrics.Values.Sum(static metrics => metrics.CacheWriteTokens)
                + uncoveredRecords.Sum(static record => record.CacheWriteTokens);
            var totalNanoAiu = _sessionMetrics.Values.Sum(static metrics => metrics.EffectiveTotalNanoAiu() ?? 0)
                + uncoveredRecords.Sum(static record => record.EffectiveTotalNanoAiu() ?? 0);
            var cost = _sessionMetrics.Values.Sum(static metrics => metrics.EffectiveCost() ?? 0)
                + uncoveredRecords.Sum(static record => record.EffectiveCost() ?? 0);
            var billingSource = ResolveBillingSource(
                _sessionMetrics.Values.Select(static metrics => metrics.BillingSource())
                    .Concat(uncoveredRecords.Select(static record => record.BillingSource())));

            return new CopilotUsageSnapshot(
                _records.Count,
                inputTokens,
                outputTokens,
                reasoningTokens,
                cacheReadTokens,
                cacheWriteTokens,
                inputTokens + outputTokens + reasoningTokens,
                totalNanoAiu > 0 ? totalNanoAiu : null,
                cost > 0 ? cost : null,
                _records.LastOrDefault().Record,
                _records.Select(static entry => entry.Record).ToArray(),
                _sessionMetrics.Values.OrderByDescending(static metrics => metrics.UpdatedAt).ToArray(),
                billingSource);
        }
    }

    public void Record(CopilotUsageRecord record)
    {
        lock (_gate)
        {
            _records.Add((++_sequence, record));
            if (_records.Count > _maxRecentRecords)
            {
                _records.RemoveRange(0, _records.Count - _maxRecentRecords);
            }
        }

        Changed?.Invoke();
    }

    internal static CopilotUsageBillingSource ResolveBillingSource(IEnumerable<CopilotUsageBillingSource> sources)
    {
        var hasUnreported = false;
        var hasSdkReported = false;
        foreach (var source in sources)
        {
            if (source is CopilotUsageBillingSource.Mixed)
            {
                hasSdkReported = true;
                hasUnreported = true;
            }
            else if (source is CopilotUsageBillingSource.SdkReported)
            {
                hasSdkReported = true;
            }
            else
            {
                hasUnreported = true;
            }
        }

        return (hasSdkReported, hasUnreported) switch
        {
            (true, true) => CopilotUsageBillingSource.Mixed,
            (true, false) => CopilotUsageBillingSource.SdkReported,
            _ => CopilotUsageBillingSource.None,
        };
    }

    public long CaptureMetricsWatermark()
    {
        lock (_gate)
        {
            return ++_sequence;
        }
    }

    public void RecordSessionMetrics(CopilotSessionUsageMetrics metrics, long? watermark = null)
    {
        lock (_gate)
        {
            var coveredSequence = watermark ?? ++_sequence;
            if (coveredSequence < _resetWatermark
                || (_metricsWatermarks.TryGetValue(metrics.SessionId, out var previousWatermark)
                    && coveredSequence <= previousWatermark))
            {
                return;
            }

            _sessionMetrics[metrics.SessionId] = metrics;
            _metricsWatermarks[metrics.SessionId] = coveredSequence;
        }

        Changed?.Invoke();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _records.Clear();
            _sessionMetrics.Clear();
            _metricsWatermarks.Clear();
            _resetWatermark = ++_sequence;
        }

        Changed?.Invoke();
    }

#pragma warning disable GHCP001 // Copilot SDK usage cost and metrics remain experimental.
    internal static CopilotUsageRecord FromAssistantUsage(
        AssistantUsageEvent usage,
        SessionPurpose purpose,
        string sessionId)
    {
        var data = usage.Data;
        return new CopilotUsageRecord(
            usage.Timestamp,
            sessionId,
            purpose.ToString(),
            data?.Model,
            data?.ApiCallId,
            data?.InputTokens ?? 0,
            data?.OutputTokens ?? 0,
            data?.ReasoningTokens ?? 0,
            data?.CacheReadTokens ?? 0,
            data?.CacheWriteTokens ?? 0,
            data?.Cost,
            data?.CopilotUsage?.TotalNanoAiu is > 0 ? data.CopilotUsage.TotalNanoAiu : null);
    }

    internal static CopilotSessionUsageMetrics FromSessionMetrics(
        UsageGetMetricsResult metrics,
        SessionPurpose purpose,
        string sessionId)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var modelMetrics = metrics.ModelMetrics?.Select(static pair =>
            new CopilotModelUsageMetrics(
                pair.Key,
                pair.Value.Usage?.InputTokens ?? 0,
                pair.Value.Usage?.OutputTokens ?? 0,
                pair.Value.Usage?.ReasoningTokens ?? 0,
                pair.Value.Usage?.CacheReadTokens ?? 0,
                pair.Value.Usage?.CacheWriteTokens ?? 0,
                pair.Value.TotalNanoAiu > 0 ? pair.Value.TotalNanoAiu : null,
                pair.Value.Requests?.Cost > 0 ? pair.Value.Requests.Cost : null,
                pair.Value.Requests?.Count ?? 0)).ToArray() ?? [];

        return new CopilotSessionUsageMetrics(
            DateTimeOffset.UtcNow,
            sessionId,
            purpose.ToString(),
            metrics.CurrentModel,
            modelMetrics.Sum(static model => model.InputTokens),
            modelMetrics.Sum(static model => model.OutputTokens),
            modelMetrics.Sum(static model => model.ReasoningTokens),
            modelMetrics.Sum(static model => model.CacheReadTokens),
            modelMetrics.Sum(static model => model.CacheWriteTokens),
            metrics.TotalNanoAiu > 0 ? metrics.TotalNanoAiu : null,
            metrics.TotalPremiumRequestCost > 0 ? metrics.TotalPremiumRequestCost : null,
            metrics.TotalUserRequests,
            metrics.LastCallInputTokens,
            metrics.LastCallOutputTokens,
            modelMetrics);
    }
#pragma warning restore GHCP001
}

public sealed record CopilotUsageRecord(
    DateTimeOffset RecordedAt,
    string SessionId,
    string Purpose,
    string? Model,
    string? ApiCallId,
    double InputTokens,
    double OutputTokens,
    double ReasoningTokens,
    double CacheReadTokens,
    double CacheWriteTokens,
    double? Cost,
    double? TotalNanoAiu)
{
    public double TotalTokens() => InputTokens + OutputTokens + ReasoningTokens;

    public double? AiCredits()
        => EffectiveTotalNanoAiu() is { } totalNanoAiu
            ? totalNanoAiu / CopilotUsageTracker.NanoAiuPerAiCredit
            : null;

    public double? EffectiveCost()
        => Cost is { } cost and > 0
            ? cost
            : null;

    public CopilotUsageBillingSource BillingSource()
    {
        if (Cost is > 0 || TotalNanoAiu is > 0)
        {
            return CopilotUsageBillingSource.SdkReported;
        }
        return CopilotUsageBillingSource.None;
    }

    public double? EffectiveTotalNanoAiu()
    {
        if (TotalNanoAiu is { } totalNanoAiu and > 0)
        {
            return totalNanoAiu;
        }

        return null;
    }
}

public sealed record CopilotUsageSnapshot(
    int TurnCount,
    double InputTokens,
    double OutputTokens,
    double ReasoningTokens,
    double CacheReadTokens,
    double CacheWriteTokens,
    double TotalTokens,
    double? TotalNanoAiu,
    double? Cost,
    CopilotUsageRecord? LastTurn,
    IReadOnlyList<CopilotUsageRecord> RecentTurns,
    IReadOnlyList<CopilotSessionUsageMetrics> SessionMetrics,
    CopilotUsageBillingSource BillingSource = CopilotUsageBillingSource.None)
{
    public double? AiCredits()
        => TotalNanoAiu is { } totalNanoAiu
            ? totalNanoAiu / CopilotUsageTracker.NanoAiuPerAiCredit
            : null;
}

public sealed record CopilotSessionUsageMetrics(
    DateTimeOffset UpdatedAt,
    string SessionId,
    string Purpose,
    string? CurrentModel,
    double InputTokens,
    double OutputTokens,
    double ReasoningTokens,
    double CacheReadTokens,
    double CacheWriteTokens,
    double? TotalNanoAiu,
    double? TotalPremiumRequestCost,
    double TotalUserRequests,
    double LastCallInputTokens,
    double LastCallOutputTokens,
    IReadOnlyList<CopilotModelUsageMetrics> ModelMetrics)
{
    public double? EffectiveTotalNanoAiu()
    {
        if (TotalNanoAiu is { } totalNanoAiu and > 0)
        {
            return totalNanoAiu;
        }

        var modelNanoAiu = ModelMetrics.Sum(static model => model.EffectiveTotalNanoAiu() ?? 0);
        return modelNanoAiu > 0 ? modelNanoAiu : null;
    }

    public double? EffectiveCost()
    {
        if (TotalPremiumRequestCost is { } cost and > 0)
        {
            return cost;
        }

        var modelsWithRequests = ModelMetrics
            .Where(static model => model.RequestCount > 0)
            .ToArray();
        if (modelsWithRequests.Length == 0
            || modelsWithRequests.Any(static model => model.RequestCost is not > 0))
        {
            return null;
        }

        return modelsWithRequests.Sum(static model => model.RequestCost.GetValueOrDefault());
    }

    public CopilotUsageBillingSource BillingSource()
    {
        if (TotalPremiumRequestCost is > 0 || TotalNanoAiu is > 0)
        {
            return CopilotUsageBillingSource.SdkReported;
        }
        return CopilotUsageTracker.ResolveBillingSource(ModelMetrics.Select(static model => model.BillingSource()));
    }
}

public sealed record CopilotModelUsageMetrics(
    string Model,
    double InputTokens,
    double OutputTokens,
    double ReasoningTokens,
    double CacheReadTokens,
    double CacheWriteTokens,
    double? TotalNanoAiu,
    double? RequestCost,
    double RequestCount)
{
    public double? EffectiveTotalNanoAiu()
    {
        if (TotalNanoAiu is { } totalNanoAiu and > 0)
        {
            return totalNanoAiu;
        }

        return null;
    }

    public CopilotUsageBillingSource BillingSource()
        => RequestCost is > 0 || TotalNanoAiu is > 0
            ? CopilotUsageBillingSource.SdkReported
            : CopilotUsageBillingSource.None;
}

public enum CopilotUsageBillingSource
{
    None,
    SdkReported,
    Mixed,
}
