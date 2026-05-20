using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotUsageTrackerTests
{
    [Fact]
    public void Record_Aggregates_Token_Breakdown()
    {
        var tracker = new CopilotUsageTracker();
        var changedCount = 0;
        tracker.Changed += () => changedCount++;

        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "MorningTriage",
            "gpt-5",
            "api-1",
            100,
            40,
            10,
            5,
            3,
            0.01,
            20_000_000,
            []));
        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 18, 10, 5, 0, TimeSpan.Zero),
            "session-2",
            "Adoption",
            "gpt-5",
            "api-2",
            200,
            50,
            0,
            7,
            2,
            0.02,
            30_000_000,
            []));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(2, snapshot.TurnCount);
        Assert.Equal(300, snapshot.InputTokens);
        Assert.Equal(90, snapshot.OutputTokens);
        Assert.Equal(10, snapshot.ReasoningTokens);
        Assert.Equal(12, snapshot.CacheReadTokens);
        Assert.Equal(5, snapshot.CacheWriteTokens);
        Assert.Equal(400, snapshot.TotalTokens);
        Assert.Equal(50_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(0.05, snapshot.AiCredits());
        Assert.Equal(0.03, snapshot.Cost);
        Assert.Equal(0.03, snapshot.LastTurn?.AiCredits());
        Assert.Equal("Adoption", snapshot.LastTurn?.Purpose);
        Assert.Equal(2, changedCount);
    }

    [Fact]
    public void Reset_Clears_Recorded_Usage()
    {
        var tracker = new CopilotUsageTracker();
        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Ask",
            "gpt-5",
            null,
            10,
            5,
            0,
            0,
            0,
            null,
            null,
            []));

        tracker.Reset();

        var snapshot = tracker.GetSnapshot();
        Assert.Equal(0, snapshot.TurnCount);
        Assert.Equal(0, snapshot.TotalTokens);
        Assert.Null(snapshot.LastTurn);
        Assert.Empty(snapshot.SessionMetrics);
    }

    [Fact]
    public void RecordSessionMetrics_Prefers_Beta4_Session_Metrics_For_Billing_Summary()
    {
        var tracker = new CopilotUsageTracker();
        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Triage",
            "gpt-5",
            null,
            100,
            40,
            0,
            0,
            0,
            0.01,
            10_000_000,
            []));

        tracker.RecordSessionMetrics(new CopilotSessionUsageMetrics(
            new DateTimeOffset(2026, 5, 18, 10, 1, 0, TimeSpan.Zero),
            "session-1",
            "Triage",
            "gpt-5",
            120,
            45,
            5,
            7,
            3,
            60_000_000,
            1.5,
            2,
            90,
            30,
            [new CopilotModelUsageMetrics("gpt-5", 120, 45, 5, 7, 3, 60_000_000, 1.5, 2)]));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(120, snapshot.InputTokens);
        Assert.Equal(45, snapshot.OutputTokens);
        Assert.Equal(5, snapshot.ReasoningTokens);
        Assert.Equal(170, snapshot.TotalTokens);
        Assert.Equal(60_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(0.06, snapshot.AiCredits());
        Assert.Equal(1.5, snapshot.Cost);
        var sessionMetrics = Assert.Single(snapshot.SessionMetrics);
        Assert.Equal(2, sessionMetrics.TotalUserRequests);
        Assert.Equal(90, sessionMetrics.LastCallInputTokens);
        Assert.Equal(30, sessionMetrics.LastCallOutputTokens);
    }

    [Fact]
    public void Record_Estimates_Ai_Credits_From_Token_Details_When_Total_Nano_Aiu_Is_Missing()
    {
        var tracker = new CopilotUsageTracker();
        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Triage",
            "gpt-5",
            null,
            1500,
            250,
            0,
            0,
            0,
            null,
            null,
            [
                new CopilotUsageTokenDetail("input", 1500, 1000, 10_000_000),
                new CopilotUsageTokenDetail("output", 250, 1000, 30_000_000),
            ]));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(50_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(0.05, snapshot.AiCredits());
        Assert.Equal(0.05, snapshot.LastTurn?.AiCredits());
    }

    [Fact]
    public void Record_Estimates_Ai_Credits_From_Official_Model_Pricing_When_Sdk_Aiu_Is_Missing()
    {
        var tracker = new CopilotUsageTracker();
        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Ask",
            "gpt-5.5",
            null,
            100,
            10,
            5,
            20,
            10,
            null,
            null,
            []));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(96_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(0.096, snapshot.AiCredits());
        Assert.Equal(0.00096, snapshot.Cost);
        Assert.Equal(CopilotUsageBillingSource.OfficialPricingTable, snapshot.BillingSource);
    }

    [Fact]
    public void Record_Estimates_Anthropic_Cache_Write_From_Official_Model_Pricing()
    {
        var tracker = new CopilotUsageTracker();
        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Adoption",
            "claude-sonnet-4.5",
            null,
            100,
            10,
            0,
            20,
            10,
            null,
            null,
            []));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(49_350_000, snapshot.TotalNanoAiu!.Value, 3);
        Assert.Equal(0.04935, snapshot.AiCredits()!.Value, 6);
    }

    [Fact]
    public void Record_Does_Not_Estimate_Ai_Credits_For_Unknown_Model()
    {
        var tracker = new CopilotUsageTracker();
        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Ask",
            "gpt-unknown",
            null,
            100,
            10,
            0,
            0,
            0,
            null,
            null,
            []));

        var snapshot = tracker.GetSnapshot();

        Assert.Null(snapshot.TotalNanoAiu);
        Assert.Null(snapshot.AiCredits());
        Assert.Equal(CopilotUsageBillingSource.None, snapshot.BillingSource);
    }

    [Theory]
    [InlineData("gpt-5.3-codex")]
    [InlineData("gpt-5-mini")]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.4-mini")]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-haiku-4.5")]
    [InlineData("gemini-3.1-pro")]
    [InlineData("gemini-3.5-flash")]
    public void CopilotModelPricing_Supports_Current_Sdk_Model_Ids(string model)
    {
        Assert.True(CopilotModelPricing.SupportsModel(model));
    }

    [Fact]
    public void RecordSessionMetrics_Estimates_From_Current_Model_When_Sdk_Aiu_Is_Missing()
    {
        var tracker = new CopilotUsageTracker();
        tracker.RecordSessionMetrics(new CopilotSessionUsageMetrics(
            new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Triage",
            "gpt-4.1",
            100,
            10,
            0,
            20,
            0,
            null,
            null,
            1,
            90,
            10,
            []));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(29_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(0.029, snapshot.AiCredits());
        Assert.Equal(CopilotUsageBillingSource.OfficialPricingTable, snapshot.BillingSource);
    }
}
