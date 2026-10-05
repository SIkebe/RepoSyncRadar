using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class SdkCopilotSessionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Assistant_Message_Wire_Event_Preserves_Response_Content(int repetitions)
    {
        var content = string.Concat(Enumerable.Repeat("\u65e5\u672c\u8a9e\U0001f600", repetitions));
        var json = $$"""
            {
                "data": { "messageId": "message-1", "content": "{{content}}" },
                "type": "assistant.message"
            }
            """;

        var assistant = Assert.IsType<AssistantMessageEvent>(SessionEvent.FromJson(json));

        Assert.Equal(content, assistant.Data.Content);
        Assert.Equal("message-1", assistant.Data.MessageId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshUsageMetrics_Retains_Later_Events_After_Failure_Then_Replaces_Them(bool billingReported)
    {
        var tracker = new CopilotUsageTracker();
        RecordUsage(tracker, billingReported: true, inputTokens: 100);
        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(100)));

        RecordUsage(tracker, billingReported);
        await RefreshAsync(tracker, _ => Task.FromException<CopilotSessionUsageMetrics>(
            new InvalidOperationException("Metrics unavailable.")));

        var snapshot = tracker.GetSnapshot();
        Assert.Equal(120, snapshot.InputTokens);
        Assert.Equal(48, snapshot.OutputTokens);
        Assert.Equal(12, snapshot.ReasoningTokens);
        Assert.Equal(18, snapshot.CacheReadTokens);
        Assert.Equal(6, snapshot.CacheWriteTokens);
        Assert.Equal(180, snapshot.TotalTokens);
        Assert.Equal(billingReported ? 120_000_000 : 100_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(billingReported ? 1.5 : 1.25, snapshot.Cost);
        Assert.Equal(billingReported ? CopilotUsageBillingSource.SdkReported : CopilotUsageBillingSource.Mixed,
            snapshot.BillingSource);
        Assert.Equal(2, snapshot.TurnCount);
        Assert.Equal(100, Assert.Single(snapshot.SessionMetrics).InputTokens);

        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(130)));

        snapshot = tracker.GetSnapshot();
        Assert.Equal(130, snapshot.InputTokens);
        Assert.Equal(52, snapshot.OutputTokens);
        Assert.Equal(13, snapshot.ReasoningTokens);
        Assert.Equal(19.5, snapshot.CacheReadTokens);
        Assert.Equal(6.5, snapshot.CacheWriteTokens);
        Assert.Equal(195, snapshot.TotalTokens);
        Assert.Equal(130_000_000, snapshot.TotalNanoAiu);
        Assert.Equal(1.625, snapshot.Cost);
        Assert.Equal(CopilotUsageBillingSource.SdkReported, snapshot.BillingSource);
        Assert.Equal(2, snapshot.RecentTurns.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshUsageMetrics_Does_Not_Add_Delayed_Completed_Call_Before_Or_After_Response(bool beforeResponse)
    {
        var tracker = new CopilotUsageTracker();
        RecordUsage(tracker, billingReported: true, inputTokens: 80, apiCallId: "earlier-call");
        var response = new TaskCompletionSource<CopilotSessionUsageMetrics>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = RefreshAsync(tracker, _ => response.Task);

        if (beforeResponse)
        {
            RecordUsage(tracker, billingReported: true, apiCallId: "completed-call");
        }
        response.SetResult(CreateMetrics(100));
        await refresh;
        if (!beforeResponse)
        {
            RecordUsage(tracker, billingReported: true, apiCallId: "completed-call");
        }

        Assert.Equal(100, tracker.GetSnapshot().InputTokens);
        Assert.Equal(100_000_000, tracker.GetSnapshot().TotalNanoAiu);
        Assert.Equal(1.25, tracker.GetSnapshot().Cost);

        RecordUsage(tracker, billingReported: true, apiCallId: "later-call");
        await RefreshAsync(tracker, _ => Task.FromException<CopilotSessionUsageMetrics>(
            new InvalidOperationException("Metrics unavailable.")));
        Assert.Equal(120, tracker.GetSnapshot().InputTokens);
        Assert.Equal(120_000_000, tracker.GetSnapshot().TotalNanoAiu);

        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(130)));
        Assert.Equal(130, tracker.GetSnapshot().InputTokens);
    }

    [Fact]
    public async Task RefreshUsageMetrics_Incomplete_Event_History_Is_A_Lower_Bound_Not_An_Additive_Estimate()
    {
        var tracker = new CopilotUsageTracker();
        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(100)));
        RecordUsage(tracker, billingReported: true);
        await RefreshAsync(tracker, _ => Task.FromException<CopilotSessionUsageMetrics>(
            new InvalidOperationException("Metrics unavailable.")));

        Assert.Equal(100, tracker.GetSnapshot().InputTokens);
        Assert.Equal(100_000_000, tracker.GetSnapshot().TotalNanoAiu);
        Assert.Single(tracker.GetSnapshot().RecentTurns);

        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(120)));
        Assert.Equal(120, tracker.GetSnapshot().InputTokens);
        Assert.Equal(120_000_000, tracker.GetSnapshot().TotalNanoAiu);
    }

    [Fact]
    public async Task RefreshUsageMetrics_Does_Not_Replace_Newer_Response_With_Older_Response()
    {
        var tracker = new CopilotUsageTracker();
        var olderResponse = new TaskCompletionSource<CopilotSessionUsageMetrics>(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderRefresh = RefreshAsync(tracker, _ => olderResponse.Task);
        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(130)));

        olderResponse.SetResult(CreateMetrics(100));
        await olderRefresh;

        Assert.Equal(130, tracker.GetSnapshot().InputTokens);
    }

    [Fact]
    public async Task RefreshUsageMetrics_Overlapping_Refreshes_Preserve_Later_Call_And_Ignore_Old_Response()
    {
        var tracker = new CopilotUsageTracker();
        RecordUsage(tracker, billingReported: true, inputTokens: 100, apiCallId: "completed-call");
        var olderResponse = new TaskCompletionSource<CopilotSessionUsageMetrics>(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderRefresh = RefreshAsync(tracker, _ => olderResponse.Task);
        RecordUsage(tracker, billingReported: true, apiCallId: "later-call");
        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(120)));

        olderResponse.SetResult(CreateMetrics(100));
        await olderRefresh;
        RecordUsage(tracker, billingReported: true, inputTokens: 100, apiCallId: "completed-call");

        Assert.Equal(120, tracker.GetSnapshot().InputTokens);
        Assert.Equal(120_000_000, tracker.GetSnapshot().TotalNanoAiu);
        Assert.Equal(1.5, tracker.GetSnapshot().Cost);
        Assert.Equal(2, tracker.GetSnapshot().RecentTurns.Count);
    }

    [Fact]
    public async Task RefreshUsageMetrics_Reset_Discards_InFlight_Response_And_Old_Coverage()
    {
        var tracker = new CopilotUsageTracker();
        RecordUsage(tracker, billingReported: true);
        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(100)));
        var response = new TaskCompletionSource<CopilotSessionUsageMetrics>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = RefreshAsync(tracker, _ => response.Task);

        tracker.Reset();
        RecordUsage(tracker, billingReported: true);
        response.SetResult(CreateMetrics(130));
        await refresh;

        var snapshot = tracker.GetSnapshot();
        Assert.Empty(snapshot.SessionMetrics);
        Assert.Equal(20, snapshot.InputTokens);
        Assert.Single(snapshot.RecentTurns);

        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(150)));
        Assert.Equal(150, tracker.GetSnapshot().InputTokens);
        Assert.Single(tracker.GetSnapshot().SessionMetrics);
    }

    [Fact]
    public async Task RefreshUsageMetrics_Propagates_Cancellation_Without_Covering_New_Events()
    {
        var tracker = new CopilotUsageTracker();
        RecordUsage(tracker, billingReported: true, inputTokens: 100);
        await RefreshAsync(tracker, _ => Task.FromResult(CreateMetrics(100)));
        RecordUsage(tracker, billingReported: true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => RefreshAsync(tracker,
            ct => Task.FromException<CopilotSessionUsageMetrics>(new OperationCanceledException(ct))));

        Assert.Equal(120, tracker.GetSnapshot().InputTokens);
    }

    private static Task RefreshAsync(
        CopilotUsageTracker tracker,
        Func<CancellationToken, Task<CopilotSessionUsageMetrics>> getMetricsAsync)
        => SdkCopilotSession.RefreshUsageMetricsAsync(
            tracker, getMetricsAsync, NullLogger.Instance, "session-1", TestContext.Current.CancellationToken);

    private static void RecordUsage(
        CopilotUsageTracker tracker, bool billingReported, double inputTokens = 20, string? apiCallId = null)
        => tracker.Record(new CopilotUsageRecord(
            DateTimeOffset.UnixEpoch, "session-1", "Triage", "gpt-test", apiCallId,
            inputTokens, inputTokens * 0.4, inputTokens * 0.1, inputTokens * 0.15, inputTokens * 0.05,
            billingReported ? inputTokens * 0.0125 : null, billingReported ? inputTokens * 1_000_000 : null));

    private static CopilotSessionUsageMetrics CreateMetrics(double inputTokens)
        => new(DateTimeOffset.UnixEpoch, "session-1", "Triage", "gpt-test",
            inputTokens, inputTokens * 0.4, inputTokens * 0.1, inputTokens * 0.15, inputTokens * 0.05,
            inputTokens * 1_000_000, inputTokens * 0.0125, 1, inputTokens, inputTokens * 0.4, []);

    [Theory]
    [InlineData(null, "tw", "cu", "explanation")]
    [InlineData("ex", null, "cu", "twitter")]
    [InlineData("ex", "tw", null, "customer")]
    public void CreateDraftBundle_Rejects_Missing_Fields(
        string? explanation,
        string? twitter,
        string? customer,
        string missingField)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SdkCopilotSession.CreateDraftBundle(explanation, twitter, customer));

        Assert.Contains(missingField, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDraftBundle_Maps_All_Fields()
    {
        var bundle = SdkCopilotSession.CreateDraftBundle("ex", "tw", "cu");

        Assert.Equal("ex", bundle.ExplanationJa);
        Assert.Equal("tw", bundle.TwitterJa);
        Assert.Equal("cu", bundle.CustomerJa);
        Assert.Empty(bundle.TeamsJa);
    }
}
