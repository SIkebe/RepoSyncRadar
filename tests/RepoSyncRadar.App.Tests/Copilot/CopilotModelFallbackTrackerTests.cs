using Microsoft.Extensions.Time.Testing;
using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotModelFallbackTrackerTests
{
    [Fact]
    public void Current_Is_Null_Until_A_Fallback_Is_Recorded()
    {
        var tracker = new CopilotModelFallbackTracker();

        Assert.Null(tracker.Current);
    }

    [Fact]
    public void RecordFallback_Publishes_Configured_And_Fallback_Models()
    {
        var occurredAt = new DateTimeOffset(2026, 10, 20, 1, 2, 3, TimeSpan.Zero);
        var tracker = new CopilotModelFallbackTracker(new FakeTimeProvider(occurredAt));
        var changed = 0;
        tracker.Changed += () => changed++;

        tracker.RecordFallback(SessionPurpose.Adoption, "gpt-5.6-luna", "claude-sonnet-5");

        Assert.Equal(1, changed);
        Assert.Equal(
            new CopilotModelFallback(occurredAt, SessionPurpose.Adoption, "gpt-5.6-luna", "claude-sonnet-5"),
            tracker.Current);
    }

    [Theory]
    [InlineData("", "claude-sonnet-5")]
    [InlineData("gpt-5.6-luna", " ")]
    public void RecordFallback_Rejects_Blank_Model_Ids(string configuredModel, string fallbackModel)
    {
        var tracker = new CopilotModelFallbackTracker();

        Assert.ThrowsAny<ArgumentException>(
            () => tracker.RecordFallback(SessionPurpose.Triage, configuredModel, fallbackModel));
        Assert.Null(tracker.Current);
    }
}
