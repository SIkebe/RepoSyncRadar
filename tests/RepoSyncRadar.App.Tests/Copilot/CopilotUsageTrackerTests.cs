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
}
