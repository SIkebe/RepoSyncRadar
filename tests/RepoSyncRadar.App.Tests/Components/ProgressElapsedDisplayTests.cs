using RepoSyncRadar.App.Components;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

public sealed class ProgressElapsedDisplayTests
{
    [Fact]
    public void Advance_Does_Not_Jump_When_Render_Is_Delayed()
    {
        var startedAt = new DateTimeOffset(2026, 5, 21, 10, 0, 0, TimeSpan.Zero);
        var now = startedAt.AddSeconds(24);

        var displayed = ProgressElapsedDisplay.Advance(TimeSpan.FromSeconds(17), startedAt, now);

        Assert.Equal(TimeSpan.FromSeconds(18), displayed);
    }

    [Fact]
    public void Advance_Keeps_Display_When_Actual_Time_Did_Not_Move_Forward()
    {
        var startedAt = new DateTimeOffset(2026, 5, 21, 10, 0, 0, TimeSpan.Zero);
        var now = startedAt.AddSeconds(17);

        var displayed = ProgressElapsedDisplay.Advance(TimeSpan.FromSeconds(17), startedAt, now);

        Assert.Equal(TimeSpan.FromSeconds(17), displayed);
    }
}