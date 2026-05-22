using GitHub.Copilot;
using GitHub.Copilot.Rpc;

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
    public const double UsdPerAiCredit = 0.01d;

    private const int _maxRecentRecords = 50;
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
            var cost = _records.Sum(static record => record.EffectiveCost() ?? 0);
            var billingSource = ResolveBillingSource(_records.Select(static record => record.BillingSource()));
            if (_sessionMetrics.Count > 0)
            {
                inputTokens = _sessionMetrics.Values.Sum(static metrics => metrics.InputTokens);
                outputTokens = _sessionMetrics.Values.Sum(static metrics => metrics.OutputTokens);
                reasoningTokens = _sessionMetrics.Values.Sum(static metrics => metrics.ReasoningTokens);
                cacheReadTokens = _sessionMetrics.Values.Sum(static metrics => metrics.CacheReadTokens);
                cacheWriteTokens = _sessionMetrics.Values.Sum(static metrics => metrics.CacheWriteTokens);
                totalNanoAiu = _sessionMetrics.Values.Sum(static metrics => metrics.EffectiveTotalNanoAiu() ?? 0);
                cost = _sessionMetrics.Values.Sum(static metrics => metrics.EffectiveCost() ?? 0);
                billingSource = ResolveBillingSource(_sessionMetrics.Values.Select(static metrics => metrics.BillingSource()));
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
                _sessionMetrics.Values.OrderByDescending(static metrics => metrics.UpdatedAt).ToArray(),
                billingSource);
        }
    }

    public void Record(CopilotUsageRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count > _maxRecentRecords)
            {
                _records.RemoveRange(0, _records.Count - _maxRecentRecords);
            }
        }

        Changed?.Invoke();
    }

    private static CopilotUsageBillingSource ResolveBillingSource(IEnumerable<CopilotUsageBillingSource> sources)
    {
        var effective = sources
            .Where(static source => source is not CopilotUsageBillingSource.None)
            .Distinct()
            .ToArray();
        return effective.Length switch
        {
            0 => CopilotUsageBillingSource.None,
            1 => effective[0],
            _ => CopilotUsageBillingSource.Mixed,
        };
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

    public double? EffectiveCost()
        => Cost is { } cost and > 0
            ? cost
            : AiCredits() * CopilotUsageTracker.UsdPerAiCredit;

    public CopilotUsageBillingSource BillingSource()
    {
        if (TotalNanoAiu is > 0)
        {
            return CopilotUsageBillingSource.SdkReported;
        }
        if (TokenDetails.Any(static detail => detail.EstimatedNanoAiu() is > 0))
        {
            return CopilotUsageBillingSource.SdkTokenDetails;
        }
        return CopilotModelPricing.EstimateNanoAiu(
            Model,
            InputTokens,
            OutputTokens,
            ReasoningTokens,
            CacheReadTokens,
            CacheWriteTokens) is > 0
            ? CopilotUsageBillingSource.OfficialPricingTable
            : CopilotUsageBillingSource.None;
    }

    public double? EffectiveTotalNanoAiu()
    {
        if (TotalNanoAiu is { } totalNanoAiu and > 0)
        {
            return totalNanoAiu;
        }

        var estimated = TokenDetails.Sum(static detail => detail.EstimatedNanoAiu() ?? 0);
        if (estimated > 0)
        {
            return estimated;
        }

        return CopilotModelPricing.EstimateNanoAiu(
            Model,
            InputTokens,
            OutputTokens,
            ReasoningTokens,
            CacheReadTokens,
            CacheWriteTokens);
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

        var estimated = ModelMetrics.Sum(static model => model.EffectiveTotalNanoAiu() ?? 0);
        if (estimated > 0)
        {
            return estimated;
        }

        return CopilotModelPricing.EstimateNanoAiu(
            CurrentModel,
            InputTokens,
            OutputTokens,
            ReasoningTokens,
            CacheReadTokens,
            CacheWriteTokens);
    }

    public double? EffectiveCost()
    {
        if (TotalPremiumRequestCost is { } cost and > 0)
        {
            return cost;
        }

        var totalNanoAiu = EffectiveTotalNanoAiu();
        return totalNanoAiu is { } value
            ? value / CopilotUsageTracker.NanoAiuPerAiCredit * CopilotUsageTracker.UsdPerAiCredit
            : null;
    }

    public CopilotUsageBillingSource BillingSource()
    {
        if (TotalNanoAiu is > 0)
        {
            return CopilotUsageBillingSource.SdkReported;
        }
        if (ModelMetrics.Any(static model => model.BillingSource() is CopilotUsageBillingSource.OfficialPricingTable)
            || CopilotModelPricing.EstimateNanoAiu(
                CurrentModel,
                InputTokens,
                OutputTokens,
                ReasoningTokens,
                CacheReadTokens,
                CacheWriteTokens) is > 0)
        {
            return CopilotUsageBillingSource.OfficialPricingTable;
        }
        return CopilotUsageBillingSource.None;
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

        return CopilotModelPricing.EstimateNanoAiu(
            Model,
            InputTokens,
            OutputTokens,
            ReasoningTokens,
            CacheReadTokens,
            CacheWriteTokens);
    }

    public CopilotUsageBillingSource BillingSource()
        => TotalNanoAiu is > 0
            ? CopilotUsageBillingSource.SdkReported
            : EffectiveTotalNanoAiu() is > 0
                ? CopilotUsageBillingSource.OfficialPricingTable
                : CopilotUsageBillingSource.None;
}

public enum CopilotUsageBillingSource
{
    None,
    SdkReported,
    SdkTokenDetails,
    OfficialPricingTable,
    Mixed,
}

internal static class CopilotModelPricing
{
    private static readonly Dictionary<string, ModelTokenPricing> _pricing = new(StringComparer.Ordinal)
    {
        [NormalizeModelKey("GPT-4.1")] = new(2.00, 0.50, 8.00),
        [NormalizeModelKey("GPT-5 mini")] = new(0.25, 0.025, 2.00),
        [NormalizeModelKey("GPT-5.2")] = new(1.75, 0.175, 14.00),
        [NormalizeModelKey("GPT-5.2-Codex")] = new(1.75, 0.175, 14.00),
        [NormalizeModelKey("GPT-5.3-Codex")] = new(1.75, 0.175, 14.00),
        [NormalizeModelKey("GPT-5.4")] = new(2.50, 0.25, 15.00),
        [NormalizeModelKey("GPT-5.4 mini")] = new(0.75, 0.075, 4.50),
        [NormalizeModelKey("GPT-5.4 nano")] = new(0.20, 0.02, 1.25),
        [NormalizeModelKey("GPT-5.5")] = new(5.00, 0.50, 30.00),
        [NormalizeModelKey("Claude Haiku 4.5")] = new(1.00, 0.10, 5.00, 1.25),
        [NormalizeModelKey("Claude Sonnet 4")] = new(3.00, 0.30, 15.00, 3.75),
        [NormalizeModelKey("Claude Sonnet 4.5")] = new(3.00, 0.30, 15.00, 3.75),
        [NormalizeModelKey("Claude Sonnet 4.6")] = new(3.00, 0.30, 15.00, 3.75),
        [NormalizeModelKey("Claude Opus 4.5")] = new(5.00, 0.50, 25.00, 6.25),
        [NormalizeModelKey("Claude Opus 4.6")] = new(5.00, 0.50, 25.00, 6.25),
        [NormalizeModelKey("Claude Opus 4.7")] = new(5.00, 0.50, 25.00, 6.25),
        [NormalizeModelKey("Gemini 2.5 Pro")] = new(1.25, 0.125, 10.00),
        [NormalizeModelKey("Gemini 3 Flash")] = new(0.50, 0.05, 3.00),
        [NormalizeModelKey("Gemini 3.1 Pro")] = new(2.00, 0.20, 12.00),
        [NormalizeModelKey("Gemini 3.5 Flash")] = new(1.50, 0.15, 9.00),
        [NormalizeModelKey("Raptor mini")] = new(0.25, 0.025, 2.00),
        [NormalizeModelKey("Goldeneye")] = new(1.25, 0.125, 10.00),
    };

    internal static double? EstimateNanoAiu(
        string? model,
        double inputTokens,
        double outputTokens,
        double reasoningTokens,
        double cacheReadTokens,
        double cacheWriteTokens)
    {
        if (string.IsNullOrWhiteSpace(model)
            || !_pricing.TryGetValue(NormalizeModelKey(model), out var pricing))
        {
            return null;
        }

        var aiCredits = EstimateAiCredits(inputTokens, pricing.InputUsdPerMillion)
            + EstimateAiCredits(outputTokens + reasoningTokens, pricing.OutputUsdPerMillion)
            + EstimateAiCredits(cacheReadTokens, pricing.CachedInputUsdPerMillion)
            + EstimateAiCredits(cacheWriteTokens, pricing.CacheWriteUsdPerMillion ?? 0);
        return aiCredits > 0
            ? aiCredits * CopilotUsageTracker.NanoAiuPerAiCredit
            : null;
    }

    internal static bool SupportsModel(string? model)
        => !string.IsNullOrWhiteSpace(model) && _pricing.ContainsKey(NormalizeModelKey(model));

    private static double EstimateAiCredits(double tokens, double usdPerMillionTokens)
        => tokens > 0 && usdPerMillionTokens > 0
            ? tokens * usdPerMillionTokens / 10_000d
            : 0;

    private static string NormalizeModelKey(string value)
    {
        var cleaned = value.Trim().ToLowerInvariant();
        var footnote = cleaned.IndexOf('[', StringComparison.Ordinal);
        if (footnote >= 0)
        {
            cleaned = cleaned[..footnote];
        }

        return cleaned
            .Replace("claude-", "claude ", StringComparison.Ordinal)
            .Replace("gpt-", "gpt ", StringComparison.Ordinal)
            .Replace("gemini-", "gemini ", StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private sealed record ModelTokenPricing(
        double InputUsdPerMillion,
        double CachedInputUsdPerMillion,
        double OutputUsdPerMillion,
        double? CacheWriteUsdPerMillion = null);
}
