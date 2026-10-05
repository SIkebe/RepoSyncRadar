using GitHub.Copilot;
using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotUsageTrackerTests
{
    [Fact]
    public void FromAssistantUsage_Preserves_Wire_Reported_Credits_Without_Session_Metrics()
    {
        var usage = Assert.IsType<AssistantUsageEvent>(SessionEvent.FromJson(
            """
            {
                "type": "assistant.usage",
                "timestamp": "2026-10-05T00:00:00Z",
                "data": {
                    "model": "gpt-unknown",
                    "apiCallId": "api-1",
                    "inputTokens": 100,
                    "outputTokens": 20,
                    "reasoningTokens": 10,
                    "copilotUsage": { "totalNanoAiu": 50000000 }
                }
            }
            """));
        var tracker = new CopilotUsageTracker();

        tracker.Record(CopilotUsageTracker.FromAssistantUsage(usage, SessionPurpose.Adoption, "session-1"));

        var snapshot = tracker.GetSnapshot();
        Assert.Equal(50_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(0.05, snapshot.AiCredits());
        Assert.Equal(130, snapshot.TotalTokens);
        Assert.Null(snapshot.Cost);
        Assert.Empty(snapshot.SessionMetrics);
        Assert.Equal(CopilotUsageBillingSource.SdkReported, snapshot.BillingSource);
        Assert.Equal("api-1", snapshot.LastTurn?.ApiCallId);
        Assert.Equal("session-1", snapshot.LastTurn?.SessionId);
        Assert.Equal("Adoption", snapshot.LastTurn?.Purpose);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void FromAssistantUsage_Does_Not_Invent_Credits_When_Usage_Is_Unreported(double? nanoAiu)
    {
        var usage = new AssistantUsageEvent
        {
            Data = new AssistantUsageData
            {
                Model = "gpt-unknown",
                InputTokens = 100,
                OutputTokens = 20,
                CopilotUsage = nanoAiu is { } value
                    ? new AssistantUsageCopilotUsage { TotalNanoAiu = value }
                    : null,
            },
        };
        var tracker = new CopilotUsageTracker();

        tracker.Record(CopilotUsageTracker.FromAssistantUsage(usage, SessionPurpose.Triage, "session-1"));

        var snapshot = tracker.GetSnapshot();
        Assert.Null(snapshot.TotalNanoAiu);
        Assert.Null(snapshot.AiCredits());
        Assert.Null(snapshot.Cost);
        Assert.Equal(120, snapshot.TotalTokens);
        Assert.Equal(CopilotUsageBillingSource.None, snapshot.BillingSource);
    }

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordSessionMetrics_Prefers_Metrics_Per_Session_And_Retains_Uncovered_Events(bool eventBillingReported)
    {
        var tracker = new CopilotUsageTracker();
        var recordedAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        tracker.Record(new CopilotUsageRecord(
            recordedAt, "session-1", "Triage", "gpt-test", "api-1",
            90, 30, 8, 4, 2, 0.5, 10_000_000));
        tracker.RecordSessionMetrics(new CopilotSessionUsageMetrics(
            recordedAt, "session-1", "Triage", "gpt-test",
            100, 40, 10, 5, 3, 100_000_000, 1.5, 1, 100, 40, []));
        tracker.Record(new CopilotUsageRecord(
            recordedAt, "session-2", "Adoption", "gpt-test", "api-2",
            10, 4, 2, 3, 1,
            eventBillingReported ? 0.25 : null,
            eventBillingReported ? 20_000_000 : null));
        tracker.Record(new CopilotUsageRecord(
            recordedAt, "session-2", "Adoption", "gpt-test", "api-3",
            20, 8, 4, 6, 2,
            eventBillingReported ? 0.5 : null,
            eventBillingReported ? 30_000_000 : null));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(130, snapshot.InputTokens);
        Assert.Equal(52, snapshot.OutputTokens);
        Assert.Equal(16, snapshot.ReasoningTokens);
        Assert.Equal(14, snapshot.CacheReadTokens);
        Assert.Equal(6, snapshot.CacheWriteTokens);
        Assert.Equal(198, snapshot.TotalTokens);
        Assert.Equal(eventBillingReported ? 150_000_000 : 100_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(eventBillingReported ? 0.15 : 0.1, snapshot.AiCredits());
        Assert.Equal(eventBillingReported ? 2.25 : 1.5, snapshot.Cost);
        Assert.Equal(eventBillingReported ? CopilotUsageBillingSource.SdkReported : CopilotUsageBillingSource.Mixed,
            snapshot.BillingSource);
        Assert.Equal(3, snapshot.TurnCount);
        Assert.Equal(3, snapshot.RecentTurns.Count);
        Assert.Equal("api-3", snapshot.LastTurn?.ApiCallId);
        Assert.Single(snapshot.SessionMetrics);

        tracker.RecordSessionMetrics(new CopilotSessionUsageMetrics(
            recordedAt.AddMinutes(1), "session-2", "Adoption", "gpt-test",
            40, 15, 7, 12, 4, 80_000_000, 1, 2, 20, 8, []));

        snapshot = tracker.GetSnapshot();

        Assert.Equal(140, snapshot.InputTokens);
        Assert.Equal(55, snapshot.OutputTokens);
        Assert.Equal(17, snapshot.ReasoningTokens);
        Assert.Equal(17, snapshot.CacheReadTokens);
        Assert.Equal(7, snapshot.CacheWriteTokens);
        Assert.Equal(212, snapshot.TotalTokens);
        Assert.Equal(180_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(0.18, snapshot.AiCredits());
        Assert.Equal(2.5, snapshot.Cost);
        Assert.Equal(CopilotUsageBillingSource.SdkReported, snapshot.BillingSource);
        Assert.Equal(3, snapshot.TurnCount);
        Assert.Equal(3, snapshot.RecentTurns.Count);
        Assert.Equal(2, snapshot.SessionMetrics.Count);
    }

    [Fact]
    public void RecordSessionMetrics_Preserves_Watermark_When_Recent_Records_Are_Trimmed()
    {
        var tracker = new CopilotUsageTracker();
        var record = new CopilotUsageRecord(
            DateTimeOffset.UnixEpoch, "session-1", "Triage", "gpt-test", null,
            1, 0, 0, 0, 0, 0.25, 1_000_000);
        for (var i = 0; i < 60; i++)
        {
            tracker.Record(record);
        }
        var watermark = tracker.CaptureMetricsWatermark();
        tracker.Record(record);
        tracker.RecordSessionMetrics(new CopilotSessionUsageMetrics(
            DateTimeOffset.UnixEpoch, "session-1", "Triage", "gpt-test",
            60, 0, 0, 0, 0, 60_000_000, 15, 1, 1, 0, []), watermark);

        var snapshot = tracker.GetSnapshot();
        Assert.Equal(50, snapshot.RecentTurns.Count);
        Assert.Equal(50, snapshot.TurnCount);
        Assert.Equal(61, snapshot.InputTokens);
        Assert.Equal(61_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(15.25, snapshot.Cost);
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
                new CopilotModelUsageMetrics("unused", 0, 0, 0, 0, 0, null, null, 0),
            ]));

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(0.75, snapshot.Cost);
        Assert.Equal(CopilotUsageBillingSource.SdkReported, snapshot.BillingSource);
    }

    [Fact]
    public void RecordSessionMetrics_Does_Not_Report_Partial_Model_Level_Premium_Request_Cost()
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
                new CopilotModelUsageMetrics("gpt-5-mini", 50, 5, 0, 0, 0, 25_000_000, null, 1),
            ]));

        var snapshot = tracker.GetSnapshot();

        Assert.Null(snapshot.Cost);
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
