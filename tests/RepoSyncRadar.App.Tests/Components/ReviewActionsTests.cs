using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="ReviewActions"/>. The component reads its dependencies via
/// the cascading <see cref="IServiceProvider"/> set by <c>Workbench</c>, so tests build a
/// small DI container around NSubstitute fakes and pass it as a cascading value.
/// </summary>
public sealed class ReviewActionsTests
{
    [Fact]
    public void Adopt_Click_Calls_Repository()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc"));
        cut.Find("[data-testid=\"review-adopt\"]").Click();

        repo.Received(1).SetReviewAsync("abc", ReviewStatus.Adopted, null, Arg.Any<CancellationToken>());
        broadcaster.Received(1).Publish();
    }

    [Fact]
    public void Reject_Requires_Reason()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc"));

        Assert.True(cut.Find("[data-testid=\"review-reject\"]").HasAttribute("disabled"));

        cut.Find("[data-testid=\"review-reject-reason\"]").Input("off-topic");
        Assert.False(cut.Find("[data-testid=\"review-reject\"]").HasAttribute("disabled"));

        cut.Find("[data-testid=\"review-reject\"]").Click();
        repo.Received(1).SetReviewAsync("abc", ReviewStatus.Rejected, "off-topic", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Renders_Clear_Action_Groups()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc"));

        Assert.NotNull(cut.Find("[data-testid=\"review-primary-actions\"]"));
        Assert.Contains("採用する", cut.Find("[data-testid=\"review-adopt\"]").TextContent);
        Assert.Contains("あとで見る", cut.Find("[data-testid=\"review-later\"]").TextContent);
        Assert.Contains("理由を付けて却下", cut.Find("[data-testid=\"review-reject\"]").TextContent);
        Assert.Contains("類似ディレクトリ", cut.Find("[data-testid=\"review-ignore-details\"]").TextContent);
    }

    [Fact]
    public void Suggests_Ignore_Patterns_From_Selected_Files()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc")
            .Add(c => c.FilePaths, [
                "content/copilot/concepts/billing.md",
                "data/reusables/actions/cache.md",
            ]));

        var suggestions = cut.FindAll("[data-testid=\"review-ignore-suggestion\"]")
            .Select(static button => button.GetAttribute("data-pattern"))
            .ToArray();

        Assert.Contains("content/copilot/concepts/**", suggestions);
        Assert.Contains("content/copilot/**", suggestions);
        Assert.Contains("data/reusables/actions/**", suggestions);
        Assert.Contains("data/reusables/**", suggestions);
    }

    [Fact]
    public void Ignore_Suggestion_Fills_Input_And_Can_Be_Submitted()
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.AddIgnoreRuleAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        repo.BulkRejectByPathPrefixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc")
            .Add(c => c.FilePaths, ["content/copilot/concepts/billing.md"]));

        var suggestion = cut.FindAll("[data-testid=\"review-ignore-suggestion\"]")
            .First(button => button.GetAttribute("data-pattern") == "content/copilot/concepts/**");
        suggestion.Click();

        var input = cut.Find("[data-testid=\"review-ignore-pattern\"]");
        Assert.Equal("content/copilot/concepts/**", input.GetAttribute("value"));

        cut.Find("[data-testid=\"review-ignore\"]").Click();

        repo.Received(1).AddIgnoreRuleAsync("content/copilot/concepts/**", "ignore-directory", Arg.Any<CancellationToken>());
        repo.Received(1).BulkRejectByPathPrefixAsync("content/copilot/concepts", "auto-ignored", Arg.Any<CancellationToken>());
        broadcaster.Received(1).Publish();
    }

    [Fact]
    public void Later_Sets_Status_And_Closes()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.TestContext();

        ReviewStatus? capturedFromCallback = null;
        var cut = ctx.RenderComponent<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc")
            .Add(c => c.Reviewed, (ReviewStatus status) => { capturedFromCallback = status; }));
        cut.Find("[data-testid=\"review-later\"]").Click();

        repo.Received(1).SetReviewAsync("abc", ReviewStatus.Later, null, Arg.Any<CancellationToken>());
        Assert.Equal(ReviewStatus.Later, capturedFromCallback);
    }

    [Fact]
    public void Ignore_Dir_Calls_Both_Apis()
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.AddIgnoreRuleAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        repo.BulkRejectByPathPrefixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.TestContext();

        ReviewStatus? capturedFromCallback = null;
        var cut = ctx.RenderComponent<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc")
            .Add(c => c.Reviewed, (ReviewStatus status) => { capturedFromCallback = status; }));
        cut.Find("[data-testid=\"review-ignore-pattern\"]").Input("aspnet/security/**");
        cut.Find("[data-testid=\"review-ignore\"]").Click();

        repo.Received(1).AddIgnoreRuleAsync("aspnet/security/**", "ignore-directory", Arg.Any<CancellationToken>());
        repo.Received(1).BulkRejectByPathPrefixAsync("aspnet/security", "auto-ignored", Arg.Any<CancellationToken>());
        broadcaster.Received(1).Publish();
        Assert.Equal(ReviewStatus.Rejected, capturedFromCallback);
    }

    [Fact]
    public void Sidebar_Receives_Broadcast()
    {
        var repo = Substitute.For<IRadarRepository>();
        var values = new Queue<IReadOnlyDictionary<ReviewStatus, int>>(
        [
            new Dictionary<ReviewStatus, int> { [ReviewStatus.Unseen] = 3, [ReviewStatus.Seen] = 0, [ReviewStatus.Adopted] = 0, [ReviewStatus.Rejected] = 0, [ReviewStatus.Later] = 0 },
            new Dictionary<ReviewStatus, int> { [ReviewStatus.Unseen] = 2, [ReviewStatus.Seen] = 0, [ReviewStatus.Adopted] = 0, [ReviewStatus.Rejected] = 0, [ReviewStatus.Later] = 0 },
        ]);
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(values.Dequeue()));

        var broadcaster = new ReviewBroadcaster();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<Sidebar>(p => p
            .AddCascadingValue<IServiceProvider>(sp));
        Assert.Equal("3", cut.Find("[data-testid=\"sidebar-count-Unseen\"]").TextContent);

        broadcaster.Publish();

        cut.WaitForAssertion(
            () => Assert.Equal("2", cut.Find("[data-testid=\"sidebar-count-Unseen\"]").TextContent),
            timeout: TimeSpan.FromSeconds(2));
    }

    private static ServiceProvider BuildServices(IRadarRepository repo, IReviewBroadcaster broadcaster)
    {
        return new ServiceCollection()
            .AddSingleton(repo)
            .AddSingleton(broadcaster)
            .BuildServiceProvider();
    }
}
