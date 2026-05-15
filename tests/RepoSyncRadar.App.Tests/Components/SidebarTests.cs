using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="Sidebar"/>. The component reads counts via the cascading
/// <see cref="IServiceProvider"/> set by <c>Workbench</c>, so tests build a small DI
/// container around an NSubstitute repository and pass it as a cascading value.
/// </summary>
public class SidebarTests
{
    [Fact]
    public void Sidebar_Shows_Counts_From_Repository()
    {
        // Arrange — repository returns Unseen=3 and Adopted=1.
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(
                new Dictionary<ReviewStatus, int>
                {
                    [ReviewStatus.Unseen] = 3,
                    [ReviewStatus.Seen] = 0,
                    [ReviewStatus.Adopted] = 1,
                    [ReviewStatus.Rejected] = 0,
                    [ReviewStatus.Later] = 0,
                }));

        var sp = new ServiceCollection()
            .AddSingleton(repo)
            .BuildServiceProvider();

        using var ctx = new Bunit.TestContext();

        // Act
        var cut = ctx.RenderComponent<Sidebar>(
            parameters => parameters.AddCascadingValue<IServiceProvider>(sp));

        // Assert — UI reflects all five buckets, with the asserted counts on Unseen / Adopted.
        Assert.Equal("3", cut.Find("[data-testid=\"sidebar-count-Unseen\"]").TextContent);
        Assert.Equal("1", cut.Find("[data-testid=\"sidebar-count-Adopted\"]").TextContent);
        Assert.Equal("0", cut.Find("[data-testid=\"sidebar-count-Seen\"]").TextContent);
        Assert.Equal("0", cut.Find("[data-testid=\"sidebar-count-Rejected\"]").TextContent);
        Assert.Equal("0", cut.Find("[data-testid=\"sidebar-count-Later\"]").TextContent);
    }

    [Fact]
    public void Sidebar_Explains_Status_Buckets()
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(
                new Dictionary<ReviewStatus, int>
                {
                    [ReviewStatus.Unseen] = 0,
                    [ReviewStatus.Seen] = 0,
                    [ReviewStatus.Adopted] = 0,
                    [ReviewStatus.Rejected] = 0,
                    [ReviewStatus.Later] = 0,
                }));

        var sp = new ServiceCollection()
            .AddSingleton(repo)
            .BuildServiceProvider();

        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<Sidebar>(
            parameters => parameters.AddCascadingValue<IServiceProvider>(sp));

        Assert.Contains("まだ確認していない", cut.Find("[data-testid=\"sidebar-description-Unseen\"]").TextContent);
        Assert.Contains("Must read", cut.Find("[data-testid=\"sidebar-description-Seen\"]").TextContent);
        Assert.Contains("共有", cut.Find("[data-testid=\"sidebar-description-Adopted\"]").TextContent);
        Assert.Contains("今回は扱わない", cut.Find("[data-testid=\"sidebar-description-Rejected\"]").TextContent);
        Assert.Contains("保留", cut.Find("[data-testid=\"sidebar-description-Later\"]").TextContent);
    }
}
