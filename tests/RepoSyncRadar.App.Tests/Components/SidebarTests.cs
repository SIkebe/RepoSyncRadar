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
[Collection("Localization")]
public class SidebarTests
{
    [Fact]
    public void Sidebar_Shows_Counts_From_Repository()
    {
        // Arrange — repository returns 未確認=3 and 注目=1.
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(
                new Dictionary<ReviewStatus, int>
                {
                    [ReviewStatus.Unseen] = 3,
                    [ReviewStatus.Seen] = 0,
                    [ReviewStatus.Adopted] = 1,
                    [ReviewStatus.Rejected] = 0,
                    [ReviewStatus.Archived] = 2,
                    [ReviewStatus.Later] = 0,
                }));

        var sp = new ServiceCollection()
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .AddSingleton(repo)
            .BuildServiceProvider();

        using var ctx = new Bunit.BunitContext();

        // Act
        var cut = ctx.Render<Sidebar>(
            parameters => parameters.AddCascadingValue<IServiceProvider>(sp));

        // Assert — UI reflects the five user-facing buckets, with Seen folded out of the sidebar.
        Assert.Equal("3", cut.Find("[data-testid=\"sidebar-count-Unseen\"]").TextContent);
        Assert.Equal("1", cut.Find("[data-testid=\"sidebar-count-Adopted\"]").TextContent);
        Assert.Equal("0", cut.Find("[data-testid=\"sidebar-count-Rejected\"]").TextContent);
        Assert.Equal("2", cut.Find("[data-testid=\"sidebar-count-Archived\"]").TextContent);
        Assert.Equal("0", cut.Find("[data-testid=\"sidebar-count-Later\"]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=\"sidebar-item-Seen\"]"));
    }

    [Fact]
    public void Sidebar_Exposes_Status_Descriptions_As_Titles()
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
                    [ReviewStatus.Archived] = 0,
                    [ReviewStatus.Later] = 0,
                }));

        var sp = new ServiceCollection()
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .AddSingleton(repo)
            .BuildServiceProvider();

        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<Sidebar>(
            parameters => parameters.AddCascadingValue<IServiceProvider>(sp));

        Assert.Contains("まだ人が判断していない", cut.Find("[data-testid=\"sidebar-item-Unseen\"]").GetAttribute("title"));
        Assert.Contains("見逃さず", cut.Find("[data-testid=\"sidebar-item-Adopted\"]").GetAttribute("title"));
        Assert.Contains("低優先度", cut.Find("[data-testid=\"sidebar-item-Rejected\"]").GetAttribute("title"));
        Assert.Contains("アクティブ", cut.Find("[data-testid=\"sidebar-item-Archived\"]").GetAttribute("title"));
        Assert.Contains("保留", cut.Find("[data-testid=\"sidebar-item-Later\"]").GetAttribute("title"));
        Assert.Empty(cut.FindAll("[data-testid^=\"sidebar-description-\"]"));
        Assert.DoesNotContain("スキム済み", cut.Markup, StringComparison.Ordinal);
    }
}
