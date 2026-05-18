using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class TriageScoringProgressTrackerTests
{
    [Fact]
    public void ReportCommitList_Shows_Total_And_Zero_Current()
    {
        var tracker = new TriageScoringProgressTracker();
        var progress = new CapturingProgress();

        using var scope = tracker.Begin(progress);
        tracker.ReportCommitList(["aaa1111111111111111111111111111111111111", "bbb2222222222222222222222222222222222222"]);

        var message = Assert.Single(progress.Messages);
        Assert.Contains("今回の未スコア未確認コミット", message, StringComparison.Ordinal);
        Assert.Contains("全 2 件", message, StringComparison.Ordinal);
        Assert.Contains("0 / 2", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportScoreSaved_Increments_Current_Position()
    {
        var tracker = new TriageScoringProgressTracker();
        var progress = new CapturingProgress();

        using var scope = tracker.Begin(progress);
        tracker.ReportCommitList(["aaa1111111111111111111111111111111111111", "bbb2222222222222222222222222222222222222"]);
        tracker.ReportScoreSaved("aaa1111111111111111111111111111111111111");
        tracker.ReportScoreSaved("bbb2222222222222222222222222222222222222");

        Assert.Contains(progress.Messages, message => message.Contains("1 / 2 件目", StringComparison.Ordinal)
            && message.Contains("aaa11111", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("2 / 2 件目", StringComparison.Ordinal)
            && message.Contains("bbb22222", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportScoreSaved_Does_Not_Count_Duplicate_Sha_Twice()
    {
        var tracker = new TriageScoringProgressTracker();
        var progress = new CapturingProgress();

        using var scope = tracker.Begin(progress);
        tracker.ReportCommitList(["aaa1111111111111111111111111111111111111"]);
        tracker.ReportScoreSaved("aaa1111111111111111111111111111111111111");
        tracker.ReportScoreSaved("aaa1111111111111111111111111111111111111");

        Assert.Single(progress.Messages, message => message.Contains("1 / 1 件目", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportCommitList_Ignores_Later_Smaller_Lists_During_Same_Triage()
    {
        var tracker = new TriageScoringProgressTracker();
        var progress = new CapturingProgress();

        using var scope = tracker.Begin(progress);
        tracker.ReportCommitList([
            "aaa1111111111111111111111111111111111111",
            "bbb2222222222222222222222222222222222222",
        ]);
        tracker.ReportCommitList(["aaa1111111111111111111111111111111111111"]);
        tracker.ReportScoreSaved("aaa1111111111111111111111111111111111111");

        Assert.DoesNotContain(progress.Messages, message => message.Contains("全 1 件", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("1 / 2 件目", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportScoreSaved_Ignores_Sha_Outside_Known_Target_List()
    {
        var tracker = new TriageScoringProgressTracker();
        var progress = new CapturingProgress();

        using var scope = tracker.Begin(progress);
        tracker.ReportCommitList(["aaa1111111111111111111111111111111111111"]);
        tracker.ReportScoreSaved("bbb2222222222222222222222222222222222222");

        Assert.DoesNotContain(progress.Messages, message => message.Contains("1 / 1 件目", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispose_Stops_Reporting_To_Completed_Triage()
    {
        var tracker = new TriageScoringProgressTracker();
        var progress = new CapturingProgress();

        using (tracker.Begin(progress))
        {
            tracker.ReportCommitList(["aaa1111111111111111111111111111111111111"]);
        }

        tracker.ReportScoreSaved("aaa1111111111111111111111111111111111111");

        Assert.Single(progress.Messages);
    }

    private sealed class CapturingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }
}