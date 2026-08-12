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
            20_000_000));
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
            30_000_000));

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
            null));

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
            10_000_000));

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
    public void Record_Does_Not_Estimate_Ai_Credits_When_Sdk_Aiu_Is_Missing()
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
            null));

        var snapshot = tracker.GetSnapshot();

        Assert.Null(snapshot.TotalNanoAiu);
        Assert.Null(snapshot.AiCredits());
        Assert.Null(snapshot.Cost);
        Assert.Equal(CopilotUsageBillingSource.None, snapshot.BillingSource);
    }

    [Fact]
    public void Record_Treats_Cost_Only_Usage_As_Sdk_Reported()
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
            0.0042,
            null));

        var snapshot = tracker.GetSnapshot();

        Assert.Null(snapshot.TotalNanoAiu);
        Assert.Equal(0.0042, snapshot.Cost);
        Assert.Equal(CopilotUsageBillingSource.SdkReported, snapshot.BillingSource);
    }

    [Fact]
    public void Record_Treats_Mixed_Reported_And_Unreported_Usage_As_Mixed()
    {
        var tracker = new CopilotUsageTracker();
        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Ask",
            "gpt-5",
            null,
            100,
            10,
            0,
            0,
            0,
            null,
            25_000_000));
        tracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 19, 10, 5, 0, TimeSpan.Zero),
            "session-2",
            "Ask",
            "gpt-unknown",
            null,
            50,
            5,
            0,
            0,
            0,
            null,
            null));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(25_000_000, snapshot.TotalNanoAiu);
        Assert.Null(snapshot.Cost);
        Assert.Equal(CopilotUsageBillingSource.Mixed, snapshot.BillingSource);
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
            null));

        var snapshot = tracker.GetSnapshot();

        Assert.Null(snapshot.TotalNanoAiu);
        Assert.Null(snapshot.AiCredits());
        Assert.Equal(CopilotUsageBillingSource.None, snapshot.BillingSource);
    }

    [Fact]
    public void RecordSessionMetrics_Does_Not_Estimate_From_Current_Model_When_Sdk_Aiu_Is_Missing()
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

        Assert.Null(snapshot.TotalNanoAiu);
        Assert.Null(snapshot.AiCredits());
        Assert.Equal(CopilotUsageBillingSource.None, snapshot.BillingSource);
    }

    [Fact]
    public void RecordSessionMetrics_Uses_Model_Level_Sdk_Aiu_When_Total_Is_Missing()
    {
        var tracker = new CopilotUsageTracker();
        tracker.RecordSessionMetrics(new CopilotSessionUsageMetrics(
            new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Triage",
            "gpt-5",
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
            [new CopilotModelUsageMetrics("gpt-5", 100, 10, 0, 20, 0, 50_000_000, null, 1)]));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(50_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(0.05, snapshot.AiCredits());
        Assert.Null(snapshot.Cost);
        Assert.Equal(CopilotUsageBillingSource.SdkReported, snapshot.BillingSource);
    }

    [Fact]
    public void RecordSessionMetrics_Uses_Model_Level_Premium_Request_Cost_When_Total_Is_Missing()
    {
        var tracker = new CopilotUsageTracker();
        tracker.RecordSessionMetrics(new CopilotSessionUsageMetrics(
            new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Triage",
            "gpt-5",
            100,
            10,
            0,
            0,
            0,
            50_000_000,
            null,
            2,
            90,
            10,
            [
                new CopilotModelUsageMetrics("gpt-5", 50, 5, 0, 0, 0, 25_000_000, 0.5, 1),
                new CopilotModelUsageMetrics("gpt-5-mini", 50, 5, 0, 0, 0, 25_000_000, 0.25, 1),
            ]));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(0.75, snapshot.Cost);
        Assert.Equal(CopilotUsageBillingSource.SdkReported, snapshot.BillingSource);
    }

    [Fact]
    public void RecordSessionMetrics_Treats_Mixed_Model_Usage_As_Mixed()
    {
        var tracker = new CopilotUsageTracker();
        tracker.RecordSessionMetrics(new CopilotSessionUsageMetrics(
            new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            "Triage",
            "gpt-5",
            150,
            15,
            0,
            0,
            0,
            null,
            null,
            2,
            100,
            10,
            [
                new CopilotModelUsageMetrics("gpt-5", 100, 10, 0, 0, 0, 50_000_000, null, 1),
                new CopilotModelUsageMetrics("gpt-unknown", 50, 5, 0, 0, 0, null, null, 1),
            ]));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(50_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(CopilotUsageBillingSource.Mixed, snapshot.BillingSource);
    }
}
