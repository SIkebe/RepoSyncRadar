using GitHub.Copilot.SDK;
using GitHub.Copilot.SDK.Rpc;

namespace RepoSyncRadar.App.Copilot;

public interface ICopilotUsageTracker
{
    event Action? Changed;

    CopilotUsageSnapshot GetSnapshot();

    void Record(CopilotUsageRecord record);

    void RecordSessionMetrics(CopilotSessionUsageMetrics metrics);

    void Reset();
}

public sealed class CopilotUsageTracker : ICopilotUsageTracker
{
    public const double NanoAiuPerAiCredit = 1_000_000_000d;

    private const int MaxRecentRecords = 50;
    private readonly object _gate = new();
    private readonly List<CopilotUsageRecord> _records = [];
    private readonly Dictionary<string, CopilotSessionUsageMetrics> _sessionMetrics = new(StringComparer.Ordinal);

    public event Action? Changed;

    public CopilotUsageSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var inputTokens = _records.Sum(static record => record.InputTokens);
            var outputTokens = _records.Sum(static record => record.OutputTokens);
            var reasoningTokens = _records.Sum(static record => record.ReasoningTokens);
            var cacheReadTokens = _records.Sum(static record => record.CacheReadTokens);
            var cacheWriteTokens = _records.Sum(static record => record.CacheWriteTokens);
            var totalNanoAiu = _records.Sum(static record => record.EffectiveTotalNanoAiu() ?? 0);
            var cost = _records.Sum(static record => record.Cost ?? 0);
            if (_sessionMetrics.Count > 0)
            {
                inputTokens = _sessionMetrics.Values.Sum(static metrics => metrics.InputTokens);
                outputTokens = _sessionMetrics.Values.Sum(static metrics => metrics.OutputTokens);
                reasoningTokens = _sessionMetrics.Values.Sum(static metrics => metrics.ReasoningTokens);
                cacheReadTokens = _sessionMetrics.Values.Sum(static metrics => metrics.CacheReadTokens);
                cacheWriteTokens = _sessionMetrics.Values.Sum(static metrics => metrics.CacheWriteTokens);
                totalNanoAiu = _sessionMetrics.Values.Sum(static metrics => metrics.TotalNanoAiu ?? 0);
                cost = _sessionMetrics.Values.Sum(static metrics => metrics.TotalPremiumRequestCost ?? 0);
            }

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
                _records.LastOrDefault(),
                _records.ToArray(),
                _sessionMetrics.Values.OrderByDescending(static metrics => metrics.UpdatedAt).ToArray());
        }
    }

    public void Record(CopilotUsageRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count > MaxRecentRecords)
            {
                _records.RemoveRange(0, _records.Count - MaxRecentRecords);
            }
        }

        Changed?.Invoke();
    }

    public void RecordSessionMetrics(CopilotSessionUsageMetrics metrics)
    {
        lock (_gate)
        {
            _sessionMetrics[metrics.SessionId] = metrics;
        }

        Changed?.Invoke();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _records.Clear();
            _sessionMetrics.Clear();
        }

        Changed?.Invoke();
    }

    internal static CopilotUsageRecord FromAssistantUsage(
        AssistantUsageEvent usage,
        SessionPurpose purpose,
        string sessionId)
    {
        var data = usage.Data;
        var copilotUsage = data?.CopilotUsage;
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
            copilotUsage?.TotalNanoAiu,
            copilotUsage?.TokenDetails?.Select(static detail =>
                new CopilotUsageTokenDetail(detail.TokenType, detail.TokenCount, detail.BatchSize, detail.CostPerBatch)).ToArray() ?? []);
    }

#pragma warning disable GHCP001 // beta.4 exposes session usage metrics as experimental.
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
    double? TotalNanoAiu,
    IReadOnlyList<CopilotUsageTokenDetail> TokenDetails)
{
    public double TotalTokens() => InputTokens + OutputTokens + ReasoningTokens;

    public double? AiCredits()
        => EffectiveTotalNanoAiu() is { } totalNanoAiu
            ? totalNanoAiu / CopilotUsageTracker.NanoAiuPerAiCredit
            : null;

    public double? EffectiveTotalNanoAiu()
    {
        if (TotalNanoAiu is { } totalNanoAiu and > 0)
        {
            return totalNanoAiu;
        }

        var estimated = TokenDetails.Sum(static detail => detail.EstimatedNanoAiu() ?? 0);
        return estimated > 0 ? estimated : null;
    }
}

public sealed record CopilotUsageTokenDetail(
    string? TokenType,
    double TokenCount,
    double BatchSize,
    double CostPerBatch)
{
    public double? EstimatedNanoAiu()
    {
        if (TokenCount <= 0 || BatchSize <= 0 || CostPerBatch <= 0)
        {
            return null;
        }

        return Math.Ceiling(TokenCount / BatchSize) * CostPerBatch;
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
    IReadOnlyList<CopilotSessionUsageMetrics> SessionMetrics)
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
    IReadOnlyList<CopilotModelUsageMetrics> ModelMetrics);

public sealed record CopilotModelUsageMetrics(
    string Model,
    double InputTokens,
    double OutputTokens,
    double ReasoningTokens,
    double CacheReadTokens,
    double CacheWriteTokens,
    double? TotalNanoAiu,
    double? RequestCost,
    double RequestCount);
