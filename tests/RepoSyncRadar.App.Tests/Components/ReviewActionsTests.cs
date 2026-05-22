using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App;
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
[Collection("Localization")]
public sealed class ReviewActionsTests : IDisposable
{
    public ReviewActionsTests()
    {
        AppDisplayCulture.Apply(AppDisplayCulture.DefaultCultureName);
    }

    public void Dispose()
    {
        AppDisplayCulture.Apply(AppDisplayCulture.DefaultCultureName);
    }

    [Fact]
    public void Adopt_Click_Calls_Repository()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc"));
        cut.Find("[data-testid=\"review-adopt\"]").Click();

        repo.Received(1).SetReviewAsync("abc", ReviewStatus.Adopted, null, Arg.Any<CancellationToken>());
        broadcaster.Received(1).Publish();
    }

    [Fact]
    public void Archive_Requires_Reason()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc"));

        Assert.True(cut.Find("[data-testid=\"review-reject\"]").HasAttribute("disabled"));

        cut.Find("[data-testid=\"review-reject-reason\"]").Input("off-topic");
        Assert.False(cut.Find("[data-testid=\"review-reject\"]").HasAttribute("disabled"));

        cut.Find("[data-testid=\"review-reject\"]").Click();
        repo.Received(1).SetReviewAsync("abc", ReviewStatus.Archived, "off-topic", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Archive_Reason_Preset_Fills_Input_And_Can_Be_Submitted()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc"));

        var preset = cut.FindAll("[data-testid=\"review-reject-reason-option\"]")
            .Single(button => button.GetAttribute("data-reason") == "既存情報のみ");
        preset.Click();

        Assert.Equal("既存情報のみ", cut.Find("[data-testid=\"review-reject-reason\"]").GetAttribute("value"));
        Assert.Contains("active", cut.Find("[data-reason=\"既存情報のみ\"]").ClassList);

        cut.Find("[data-testid=\"review-reject\"]").Click();

        repo.Received(1).SetReviewAsync("abc", ReviewStatus.Archived, "既存情報のみ", Arg.Any<CancellationToken>());
        broadcaster.Received(1).Publish();
    }

    [Fact]
    public void Archived_Commit_Shows_Saved_Reason()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc")
            .Add(c => c.CurrentStatus, ReviewStatus.Archived)
            .Add(c => c.CurrentReviewReason, "もう確認したから"));

        var reason = cut.Find("[data-testid=\"review-archived-reason\"]");
        Assert.Contains("アーカイブ済みの理由", reason.TextContent);
        Assert.Contains("もう確認したから", reason.TextContent);
    }

    [Fact]
    public void Renders_Clear_Action_Groups()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc"));

        Assert.NotNull(cut.Find("[data-testid=\"review-primary-actions\"]"));
        Assert.Contains("注目する", cut.Find("[data-testid=\"review-adopt\"]").TextContent);
        Assert.Contains("保留する", cut.Find("[data-testid=\"review-later\"]").TextContent);
        Assert.Contains("確認済み", cut.Find("[data-testid=\"review-reject-reason-options\"]").TextContent);
        Assert.Contains("既存情報のみ", cut.Find("[data-testid=\"review-reject-reason-options\"]").TextContent);
        Assert.Contains("アーカイブする", cut.Find("[data-testid=\"review-reject\"]").TextContent);
        Assert.Contains("類似ディレクトリ", cut.Find("[data-testid=\"review-ignore-details\"]").TextContent);
    }

    [Fact]
    public void Renders_Clear_Action_Groups_In_English()
    {
        AppDisplayCulture.Apply("en");
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc")
            .Add(c => c.FilePaths, ["content/copilot/concepts/billing.md"]));

        Assert.Contains("Review Decision", cut.Find("[data-testid=\"review-actions\"]").TextContent);
        Assert.Contains("Watch", cut.Find("[data-testid=\"review-adopt\"]").TextContent);
        Assert.Contains("Defer", cut.Find("[data-testid=\"review-later\"]").TextContent);
        Assert.Contains("Already reviewed", cut.Find("[data-testid=\"review-reject-reason-options\"]").TextContent);
        Assert.Contains("Existing information only", cut.Find("[data-testid=\"review-reject-reason-options\"]").TextContent);
        Assert.Contains("Archive", cut.Find("[data-testid=\"review-reject\"]").TextContent);
        Assert.Contains("Automatically skip similar directories", cut.Find("[data-testid=\"review-ignore-details\"]").TextContent);
        Assert.Contains("Suggestions", cut.Find("[data-testid=\"review-ignore-suggestions\"]").TextContent);
        Assert.Contains("1 match", cut.Find("[data-testid=\"review-ignore-suggestions\"]").TextContent);
        Assert.Contains("Path pattern", cut.Find("[data-testid=\"review-ignore-details\"]").TextContent);
        Assert.Equal("e.g. aspnet/security/**", cut.Find("[data-testid=\"review-ignore-pattern\"]").GetAttribute("placeholder"));
        Assert.Contains("Add to ignore list", cut.Find("[data-testid=\"review-ignore\"]").TextContent);
    }

    [Fact]
    public void Suggests_Ignore_Patterns_From_Selected_Files()
    {
        var repo = Substitute.For<IRadarRepository>();
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
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
    public void Ignore_Suggestions_Mark_Patterns_Already_In_Ignore_List()
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([
                new IgnoreRule
                {
                    Pattern = "content/copilot/concepts/**",
                    Reason = "ignore-directory",
                    CreatedAt = new DateTime(2026, 5, 16, 0, 0, 0, DateTimeKind.Utc),
                },
            ]));
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc")
            .Add(c => c.FilePaths, ["content/copilot/concepts/billing.md"]));

        cut.WaitForAssertion(() =>
        {
            var registered = cut.FindAll("[data-testid=\"review-ignore-suggestion\"]")
                .Single(button => button.GetAttribute("data-pattern") == "content/copilot/concepts/**");
            var newCandidate = cut.FindAll("[data-testid=\"review-ignore-suggestion\"]")
                .Single(button => button.GetAttribute("data-pattern") == "content/copilot/**");

            Assert.Equal("true", registered.GetAttribute("data-ignored"));
            Assert.Contains("already-ignored", registered.ClassList);
            Assert.True(registered.HasAttribute("disabled"));
            Assert.Contains("登録済み", registered.TextContent, StringComparison.Ordinal);
            Assert.Equal("false", newCandidate.GetAttribute("data-ignored"));
            Assert.False(newCandidate.HasAttribute("disabled"));
            Assert.DoesNotContain("登録済み", newCandidate.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Already_Ignored_Suggestion_Cannot_Be_Selected()
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([
                new IgnoreRule
                {
                    Pattern = "content/copilot/**",
                    Reason = "ignore-directory",
                    CreatedAt = new DateTime(2026, 5, 16, 0, 0, 0, DateTimeKind.Utc),
                },
            ]));
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc")
            .Add(c => c.FilePaths, ["content/copilot/concepts/billing.md"]));

        cut.WaitForAssertion(() =>
        {
            var registered = cut.FindAll("[data-testid=\"review-ignore-suggestion\"]")
                .Single(button => button.GetAttribute("data-pattern") == "content/copilot/**");
            Assert.True(registered.HasAttribute("disabled"));
        });

        cut.Find("[data-pattern=\"content/copilot/**\"]").Click();

        Assert.Equal(string.Empty, cut.Find("[data-testid=\"review-ignore-pattern\"]").GetAttribute("value"));
    }

    [Fact]
    public void Suggestion_Covered_By_Existing_Broader_Ignore_Rule_Is_Disabled()
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([
                new IgnoreRule
                {
                    Pattern = "content/copilot/**",
                    Reason = "ignore-directory",
                    CreatedAt = new DateTime(2026, 5, 16, 0, 0, 0, DateTimeKind.Utc),
                },
            ]));
        var broadcaster = Substitute.For<IReviewBroadcaster>();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "abc")
            .Add(c => c.FilePaths, ["content/copilot/concepts/billing.md"]));

        cut.WaitForAssertion(() =>
        {
            var covered = cut.FindAll("[data-testid=\"review-ignore-suggestion\"]")
                .Single(button => button.GetAttribute("data-pattern") == "content/copilot/concepts/**");
            var registered = cut.FindAll("[data-testid=\"review-ignore-suggestion\"]")
                .Single(button => button.GetAttribute("data-pattern") == "content/copilot/**");

            Assert.Equal("true", covered.GetAttribute("data-ignored"));
            Assert.True(covered.HasAttribute("disabled"));
            Assert.Contains("カバー済み", covered.TextContent, StringComparison.Ordinal);
            Assert.Contains("登録済み", registered.TextContent, StringComparison.Ordinal);
        });

        cut.Find("[data-pattern=\"content/copilot/concepts/**\"]").Click();

        Assert.Equal(string.Empty, cut.Find("[data-testid=\"review-ignore-pattern\"]").GetAttribute("value"));
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
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ReviewActions>(p => p
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
        using var ctx = new Bunit.BunitContext();

        ReviewStatus? capturedFromCallback = null;
        var cut = ctx.Render<ReviewActions>(p => p
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
        using var ctx = new Bunit.BunitContext();

        ReviewStatus? capturedFromCallback = null;
        var cut = ctx.Render<ReviewActions>(p => p
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
            new Dictionary<ReviewStatus, int> { [ReviewStatus.Unseen] = 3, [ReviewStatus.Seen] = 0, [ReviewStatus.Adopted] = 0, [ReviewStatus.Rejected] = 0, [ReviewStatus.Archived] = 0, [ReviewStatus.Later] = 0 },
            new Dictionary<ReviewStatus, int> { [ReviewStatus.Unseen] = 2, [ReviewStatus.Seen] = 0, [ReviewStatus.Adopted] = 0, [ReviewStatus.Rejected] = 0, [ReviewStatus.Archived] = 0, [ReviewStatus.Later] = 0 },
        ]);
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(values.Dequeue()));

        var broadcaster = new ReviewBroadcaster();
        var sp = BuildServices(repo, broadcaster);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<Sidebar>(p => p
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
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .AddSingleton(repo)
            .AddSingleton(broadcaster)
            .BuildServiceProvider();
    }
}
