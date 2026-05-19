using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using NSubstitute;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.App.Settings;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit coverage for Workbench-level selection and queue behavior that spans
/// Sidebar, CommitList, CommitDetail, and ReviewActions.
/// </summary>
public sealed class WorkbenchTests
{
    [Theory]
    [InlineData(DocsThemeMode.Dark, "radar-shell radar-theme-dark", "dark")]
    [InlineData(DocsThemeMode.Light, "radar-shell radar-theme-light", "light")]
    public void Theme_Class_And_Name_Follow_DocsThemeMode(
        DocsThemeMode theme,
        string expectedClass,
        string expectedThemeName)
    {
        Assert.Equal(expectedClass, Workbench.BuildShellClass(theme));
        Assert.Equal(expectedThemeName, Workbench.BuildThemeName(theme));
    }

    [Theory]
    [InlineData(DocsThemeMode.Dark, "radar-theme-dark", "dark")]
    [InlineData(DocsThemeMode.Light, "radar-theme-light", "light")]
    public async Task Workbench_Renders_Theme_From_User_Settings(
        DocsThemeMode theme,
        string expectedClass,
        string expectedThemeName)
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(CountsFor(ReviewStatus.Unseen)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>([]));
        var settingsStore = Substitute.For<IAppUserSettingsStore>();
        settingsStore.Current.Returns(new AppUserSettings { DefaultDocsTheme = theme });

        await using var ctx = CreateWorkbenchTestContext(repo, out _, settingsStore);
        var cut = ctx.Render<Workbench>();

        var shell = cut.Find("[data-testid=\"radar-shell\"]");
        Assert.Contains(expectedClass, shell.GetAttribute("class"), StringComparison.Ordinal);
        Assert.Equal(expectedThemeName, shell.GetAttribute("data-theme"));
    }

    [Fact]
    public async Task Workbench_Renders_Resizable_Three_Column_Shell()
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(CountsFor(ReviewStatus.Unseen)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>([]));

        var broadcaster = new ReviewBroadcaster();
        var auth = Substitute.For<IGitHubAuthSession>();
        auth.GetStateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GitHubAuthState.SignedIn));
        auth.GetCurrentLoginAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("octocat"));

        await using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services
            .AddSingleton(repo)
            .AddSingleton<IReviewBroadcaster>(broadcaster)
            .AddSingleton(auth)
            .AddSingleton(Substitute.For<ICopilotAgent>())
            .AddSingleton(Substitute.For<IPathToUrlResolver>())
            .AddSingleton<IOptions<DocsApiOptions>>(Options.Create(new DocsApiOptions
            {
                BaseAddress = new Uri("https://docs.github.com/"),
            }));

        var cut = ctx.Render<Workbench>();

        var shell = cut.Find("[data-testid=\"radar-shell\"]");
        var splitter = cut.Find("[data-testid=\"radar-sidebar-resizer\"]");
        Assert.Contains("radar-shell", shell.ClassList);
        Assert.Equal("separator", splitter.GetAttribute("role"));
        Assert.Equal("vertical", splitter.GetAttribute("aria-orientation"));
        Assert.Equal("0", splitter.GetAttribute("tabindex"));
        Assert.Contains("幅を変更", splitter.GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskPalette_Starts_Collapsed_And_Can_Be_Expanded()
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(CountsFor(ReviewStatus.Unseen)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>([]));

        await using var ctx = CreateWorkbenchTestContext(repo, out _);
        var cut = ctx.Render<Workbench>();

        Assert.Empty(cut.FindAll("[data-testid=\"ask-palette\"]"));
        var toggle = cut.Find("[data-testid=\"ask-palette-toggle\"]");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));

        toggle.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=\"ask-palette\"]"));
            Assert.Equal("true", cut.Find("[data-testid=\"ask-palette-toggle\"]").GetAttribute("aria-expanded"));
        });
    }

    [Fact]
    public async Task Archiving_Selected_Commit_Returns_To_Unseen_Queue_And_Clears_Detail()
    {
        var target = new Commit
        {
            Sha = "abc1234abc1234abc1234abc1234abc1234abc1",
            PrNumber = 61071,
            Message = "Update docs changelog",
            Author = "docs-bot",
            AuthoredAt = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
        };
        var currentStatus = ReviewStatus.Unseen;

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(CountsFor(currentStatus)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> commits = filter.Status == ReviewStatus.Unseen && currentStatus == ReviewStatus.Unseen
                    ? [target]
                    : [];
                return Task.FromResult(commits);
            });
        repo.SetReviewAsync(target.Sha, Arg.Any<ReviewStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                currentStatus = call.ArgAt<ReviewStatus>(1);
                return Task.CompletedTask;
            });

        var broadcaster = new ReviewBroadcaster();
        var auth = Substitute.For<IGitHubAuthSession>();
        auth.GetStateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GitHubAuthState.SignedIn));
        auth.GetCurrentLoginAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("octocat"));
        var resolver = Substitute.For<IPathToUrlResolver>();

        await using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services
            .AddSingleton(repo)
            .AddSingleton<IReviewBroadcaster>(broadcaster)
            .AddSingleton(auth)
            .AddSingleton(Substitute.For<ICopilotAgent>())
            .AddSingleton(resolver)
            .AddSingleton<IOptions<DocsApiOptions>>(Options.Create(new DocsApiOptions
            {
                BaseAddress = new Uri("https://docs.github.com/"),
            }));

        var cut = ctx.Render<Workbench>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]")));

        cut.Find("[data-testid=\"commit-row\"]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=\"review-reject-reason\"]"));
        cut.Find("[data-testid=\"review-reject-reason\"]").Input("対象外");
        cut.Find("[data-testid=\"review-reject\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Empty(cut.FindAll("[data-testid=\"review-actions\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"commit-detail-empty\"]"));
            Assert.Contains("active", cut.Find("[data-testid=\"sidebar-item-Unseen\"]").ClassList);
            Assert.Contains("1 件をアーカイブに移動しました", cut.Find("[data-testid=\"bulk-review-status\"]").TextContent, StringComparison.Ordinal);
        });
        await repo.Received(1).SetReviewAsync(target.Sha, ReviewStatus.Archived, "対象外", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Changing_Non_Unseen_Commit_To_Later_Keeps_Current_Queue_And_Clears_Selection()
    {
        var target = new Commit
        {
            Sha = "def5678def5678def5678def5678def5678def5",
            PrNumber = 61072,
            Message = "Update reusable snippet",
            Author = "docs-bot",
            AuthoredAt = new DateTime(2026, 5, 15, 1, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 15, 1, 0, 0, DateTimeKind.Utc),
        };
        var currentStatus = ReviewStatus.Rejected;

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(CountsFor(currentStatus)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> commits = filter.Status == currentStatus ? [target] : [];
                return Task.FromResult(commits);
            });
        repo.SetReviewAsync(target.Sha, Arg.Any<ReviewStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                currentStatus = call.ArgAt<ReviewStatus>(1);
                return Task.CompletedTask;
            });

        var broadcaster = new ReviewBroadcaster();
        var auth = Substitute.For<IGitHubAuthSession>();
        auth.GetStateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GitHubAuthState.SignedIn));
        auth.GetCurrentLoginAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("octocat"));
        var resolver = Substitute.For<IPathToUrlResolver>();

        await using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services
            .AddSingleton(repo)
            .AddSingleton<IReviewBroadcaster>(broadcaster)
            .AddSingleton(auth)
            .AddSingleton(Substitute.For<ICopilotAgent>())
            .AddSingleton(resolver)
            .AddSingleton<IOptions<DocsApiOptions>>(Options.Create(new DocsApiOptions
            {
                BaseAddress = new Uri("https://docs.github.com/"),
            }));

        var cut = ctx.Render<Workbench>();
        cut.Find("[data-testid=\"sidebar-item-Rejected\"]").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]")));

        cut.Find("[data-testid=\"commit-row\"]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=\"review-later\"]"));
        cut.Find("[data-testid=\"review-later\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("active", cut.Find("[data-testid=\"sidebar-item-Rejected\"]").ClassList);
            Assert.DoesNotContain("active", cut.Find("[data-testid=\"sidebar-item-Later\"]").ClassList);
            Assert.DoesNotContain("active", cut.Find("[data-testid=\"sidebar-item-Unseen\"]").ClassList);
            Assert.Empty(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Empty(cut.FindAll("[data-testid=\"review-actions\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"commit-detail-empty\"]"));
            Assert.Contains("1 件を保留に移動しました", cut.Find("[data-testid=\"bulk-review-status\"]").TextContent, StringComparison.Ordinal);
        });
        await repo.Received(1).SetReviewAsync(target.Sha, ReviewStatus.Later, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clicking_Active_Status_Keeps_Filtered_Queue()
    {
        var rejected = MakeWorkbenchCommit("aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", "rejected docs");
        var adopted = MakeWorkbenchCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", "adopted docs");
        var statuses = new Dictionary<string, ReviewStatus>(StringComparer.Ordinal)
        {
            [rejected.Sha] = ReviewStatus.Rejected,
            [adopted.Sha] = ReviewStatus.Adopted,
        };
        var commits = new[] { rejected, adopted };

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(statuses.Values)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = commits
                    .Where(commit => filter.Status is null || statuses[commit.Sha] == filter.Status)
                    .ToArray();
                return Task.FromResult(visible);
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out _);
        var cut = ctx.Render<Workbench>();

        cut.Find("[data-testid=\"sidebar-item-Rejected\"]").Click();
        cut.WaitForAssertion(() =>
        {
            var row = Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Equal(rejected.Sha, row.GetAttribute("data-sha"));
            Assert.Contains("active", cut.Find("[data-testid=\"sidebar-item-Rejected\"]").ClassList);
        });

        cut.Find("[data-testid=\"sidebar-item-Rejected\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var row = Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Equal(rejected.Sha, row.GetAttribute("data-sha"));
            Assert.Contains("active", cut.Find("[data-testid=\"sidebar-item-Rejected\"]").ClassList);
            Assert.DoesNotContain(adopted.Sha, cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Commit_Hash_Search_Filters_Visible_Queue_And_Clear_Restores()
    {
        var first = MakeWorkbenchCommit("aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", "first docs");
        var second = MakeWorkbenchCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", "second docs");
        var commits = new[] { first, second };

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(commits.Select(static _ => ReviewStatus.Unseen))));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = commits
                    .Where(commit => filter.Status is null or ReviewStatus.Unseen)
                    .Where(commit => CommitMatchesSearch(commit, filter.ShaQuery))
                    .ToArray();
                return Task.FromResult(visible);
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out _);
        var cut = ctx.Render<Workbench>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=\"commit-row\"]").Count));

        cut.Find("[data-testid=\"commit-hash-search\"]").Input("BBB2222");

        cut.WaitForAssertion(() =>
        {
            var row = Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Equal(second.Sha, row.GetAttribute("data-sha"));
        });

        cut.Find("[data-testid=\"commit-hash-search-clear\"]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=\"commit-row\"]").Count));
    }

    [Fact]
    public async Task Commit_Search_Matches_Pr_Number_And_Message()
    {
        var first = MakeWorkbenchCommit("aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", "first docs");
        first.PrNumber = 61001;
        var second = MakeWorkbenchCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", "workspace rollout guide");
        second.PrNumber = 61002;
        var commits = new[] { first, second };

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(commits.Select(static _ => ReviewStatus.Unseen))));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = commits
                    .Where(commit => filter.Status is null or ReviewStatus.Unseen)
                    .Where(commit => CommitMatchesSearch(commit, filter.ShaQuery))
                    .ToArray();
                return Task.FromResult(visible);
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out _);
        var cut = ctx.Render<Workbench>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=\"commit-row\"]").Count));

        cut.Find("[data-testid=\"commit-hash-search\"]").Input("61002");
        cut.WaitForAssertion(() =>
        {
            var row = Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Equal(second.Sha, row.GetAttribute("data-sha"));
        });

        cut.Find("[data-testid=\"commit-hash-search\"]").Input("WORKSPACE");
        cut.WaitForAssertion(() =>
        {
            var row = Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Equal(second.Sha, row.GetAttribute("data-sha"));
        });
    }

    [Fact]
    public async Task Bulk_Move_Selected_Unseen_Commits_To_Later_Updates_Each_Review()
    {
        var commits = new List<Commit>
        {
            MakeWorkbenchCommit("aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", "first"),
            MakeWorkbenchCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", "second"),
            MakeWorkbenchCommit("ccc3333ccc3333ccc3333ccc3333ccc3333ccc3", "third"),
        };
        var statuses = commits.ToDictionary(static commit => commit.Sha, _ => ReviewStatus.Unseen, StringComparer.Ordinal);

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(statuses.Values)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = commits
                    .Where(commit => filter.Status is null || statuses[commit.Sha] == filter.Status)
                    .ToArray();
                return Task.FromResult(visible);
            });
        repo.SetReviewAsync(Arg.Any<string>(), Arg.Any<ReviewStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                statuses[call.ArgAt<string>(0)] = call.ArgAt<ReviewStatus>(1);
                return Task.CompletedTask;
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out _);
        var cut = ctx.Render<Workbench>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=\"commit-row\"]").Count));

        cut.FindAll("[data-testid=\"commit-select\"]")[0].Change(true);
        cut.FindAll("[data-testid=\"commit-select\"]")[1].Change(true);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("2 件選択中", cut.Find("[data-testid=\"bulk-review-count\"]").TextContent, StringComparison.Ordinal);
            Assert.Contains("まとめて移動", cut.Find("[data-testid=\"bulk-review-hint\"]").TextContent, StringComparison.Ordinal);
            Assert.Contains("行をクリック", cut.Find("[data-testid=\"bulk-review-hint\"]").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=\"bulk-review-rejected\"]"));
            Assert.DoesNotContain("見送り候補へ", cut.Markup, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=\"bulk-review-unseen\"]"));
        });

        cut.Find("[data-testid=\"bulk-review-later\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Contains("2 件を保留に移動しました", cut.Find("[data-testid=\"bulk-review-status\"]").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("checked", cut.Markup, StringComparison.Ordinal);
        });
        await repo.Received(1).SetReviewAsync(commits[0].Sha, ReviewStatus.Later, null, Arg.Any<CancellationToken>());
        await repo.Received(1).SetReviewAsync(commits[1].Sha, ReviewStatus.Later, null, Arg.Any<CancellationToken>());
        await repo.DidNotReceive().SetReviewAsync(commits[2].Sha, Arg.Any<ReviewStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bulk_Move_Selected_Auto_Rejected_Commits_To_Archived()
    {
        var commits = new List<Commit>
        {
            MakeWorkbenchCommit("aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", "first auto rejected"),
            MakeWorkbenchCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", "second auto rejected"),
        };
        var statuses = commits.ToDictionary(static commit => commit.Sha, _ => ReviewStatus.Rejected, StringComparer.Ordinal);

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(statuses.Values)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = commits
                    .Where(commit => filter.Status is null || statuses[commit.Sha] == filter.Status)
                    .ToArray();
                return Task.FromResult(visible);
            });
        repo.SetReviewAsync(Arg.Any<string>(), Arg.Any<ReviewStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                statuses[call.ArgAt<string>(0)] = call.ArgAt<ReviewStatus>(1);
                return Task.CompletedTask;
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out _);
        var cut = ctx.Render<Workbench>();
        cut.Find("[data-testid=\"sidebar-item-Rejected\"]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=\"commit-row\"]").Count));

        cut.Find("[data-testid=\"commit-list-select-all\"]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find("[data-testid=\"bulk-review-archived\"]").HasAttribute("disabled"));
            Assert.Empty(cut.FindAll("[data-testid=\"bulk-review-rejected\"]"));
        });

        cut.Find("[data-testid=\"bulk-review-archived\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("active", cut.Find("[data-testid=\"sidebar-item-Rejected\"]").ClassList);
            Assert.DoesNotContain("active", cut.Find("[data-testid=\"sidebar-item-Archived\"]").ClassList);
            Assert.Empty(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Contains("2 件をアーカイブに移動しました", cut.Find("[data-testid=\"bulk-review-status\"]").TextContent, StringComparison.Ordinal);
            Assert.NotNull(cut.Find("[data-testid=\"commit-detail-empty\"]"));
        });
        await repo.Received(1).SetReviewAsync(commits[0].Sha, ReviewStatus.Archived, null, Arg.Any<CancellationToken>());
        await repo.Received(1).SetReviewAsync(commits[1].Sha, ReviewStatus.Archived, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bulk_Delete_Selected_Unseen_Commits_Removes_Them_From_Local_Inbox()
    {
        var commits = new List<Commit>
        {
            MakeWorkbenchCommit("aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", "first"),
            MakeWorkbenchCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", "second"),
            MakeWorkbenchCommit("ccc3333ccc3333ccc3333ccc3333ccc3333ccc3", "third"),
        };
        var statuses = commits.ToDictionary(static commit => commit.Sha, _ => ReviewStatus.Unseen, StringComparer.Ordinal);

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(statuses.Values)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = commits
                    .Where(commit => filter.Status is null || statuses[commit.Sha] == filter.Status)
                    .ToArray();
                return Task.FromResult(visible);
            });
        repo.DeleteUnseenCommitsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var shas = call.Arg<IEnumerable<string>>().ToHashSet(StringComparer.Ordinal);
                var deleted = commits.RemoveAll(commit => shas.Contains(commit.Sha));
                foreach (var sha in shas)
                {
                    statuses.Remove(sha);
                }
                return deleted;
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out _);
        var cut = ctx.Render<Workbench>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=\"commit-row\"]").Count));

        cut.FindAll("[data-testid=\"commit-select\"]")[0].Change(true);
        cut.FindAll("[data-testid=\"commit-select\"]")[1].Change(true);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=\"bulk-delete-unseen\"]").HasAttribute("disabled")));

        cut.Find("[data-testid=\"bulk-delete-unseen\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var row = Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Equal(commits[0].Sha, row.GetAttribute("data-sha"));
            Assert.Contains("2 件をローカル DB から削除しました", cut.Find("[data-testid=\"bulk-review-status\"]").TextContent, StringComparison.Ordinal);
            Assert.NotNull(cut.Find("[data-testid=\"commit-detail-empty\"]"));
        });
        var expectedDeletedShas = new[]
        {
            "aaa1111aaa1111aaa1111aaa1111aaa1111aaa1",
            "bbb2222bbb2222bbb2222bbb2222bbb2222bbb2",
        };
        await repo.Received(1).DeleteUnseenCommitsAsync(
            Arg.Is<IEnumerable<string>>(shas => shas.SequenceEqual(expectedDeletedShas)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bulk_Explain_Selected_Adopted_Commits_Generates_Individual_Drafts()
    {
        var commits = new List<Commit>
        {
            CloneWorkbenchCommit(MakeWorkbenchCommit("aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", "first adopted"), ReviewStatus.Adopted),
            CloneWorkbenchCommit(MakeWorkbenchCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", "second adopted"), ReviewStatus.Adopted),
            CloneWorkbenchCommit(MakeWorkbenchCommit("ccc3333ccc3333ccc3333ccc3333ccc3333ccc3", "third adopted"), ReviewStatus.Adopted),
        };
        var statuses = commits.ToDictionary(static commit => commit.Sha, _ => ReviewStatus.Adopted, StringComparer.Ordinal);
        var broadcaster = new ReviewBroadcaster();
        var published = false;
        broadcaster.Reviewed += (_, _) => published = true;

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(statuses.Values)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = commits
                    .Where(commit => filter.Status is null || statuses[commit.Sha] == filter.Status)
                    .ToArray();
                return Task.FromResult(visible);
            });
        var agent = Substitute.For<ICopilotAgent>();
        agent.GenerateBatchExplanationAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(2));

        await using var ctx = CreateWorkbenchTestContext(repo, out _, agent: agent, existingBroadcaster: broadcaster);
        var cut = ctx.Render<Workbench>();
        cut.Find("[data-testid=\"sidebar-item-Adopted\"]").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=\"commit-row\"]").Count));

        cut.FindAll("[data-testid=\"commit-select\"]")[0].Change(true);
        cut.FindAll("[data-testid=\"commit-select\"]")[1].Change(true);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=\"bulk-explain-adopted\"]").HasAttribute("disabled")));

        cut.Find("[data-testid=\"bulk-explain-adopted\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("2 件の注目コミットの個別解説を生成しました", cut.Find("[data-testid=\"batch-explanation-status\"]").TextContent, StringComparison.Ordinal);
            Assert.True(published);
        });
        await agent.Received(1).GenerateBatchExplanationAsync(
            Arg.Is<IReadOnlyList<string>>(shas => shas.SequenceEqual(new[] { commits[0].Sha, commits[1].Sha })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bulk_Explain_Selected_Adopted_Commits_Shows_Progress_And_Can_Cancel()
    {
        var commits = new List<Commit>
        {
            CloneWorkbenchCommit(MakeWorkbenchCommit("aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", "first adopted"), ReviewStatus.Adopted),
            CloneWorkbenchCommit(MakeWorkbenchCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", "second adopted"), ReviewStatus.Adopted),
        };
        var statuses = commits.ToDictionary(static commit => commit.Sha, _ => ReviewStatus.Adopted, StringComparer.Ordinal);

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(statuses.Values)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = commits
                    .Where(commit => filter.Status is null || statuses[commit.Sha] == filter.Status)
                    .ToArray();
                return Task.FromResult(visible);
            });
        CancellationToken capturedToken = default;
        var agent = Substitute.For<ICopilotAgent>();
        agent.GenerateBatchExplanationAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedToken = call.Arg<CancellationToken>();
                var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                capturedToken.Register(() => pending.TrySetCanceled(capturedToken));
                return pending.Task;
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out _, agent: agent);
        var cut = ctx.Render<Workbench>();
        cut.Find("[data-testid=\"sidebar-item-Adopted\"]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=\"commit-row\"]").Count));

        cut.Find("[data-testid=\"commit-list-select-all\"]").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=\"bulk-explain-adopted\"]").HasAttribute("disabled")));
        cut.Find("[data-testid=\"bulk-explain-adopted\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Copilot が注目コミットの解説を個別生成中", cut.Find("[data-testid=\"batch-explanation-progress-text\"]").TextContent, StringComparison.Ordinal);
            Assert.False(cut.Find("[data-testid=\"batch-explanation-cancel\"]").HasAttribute("disabled"));
        });

        cut.Find("[data-testid=\"batch-explanation-cancel\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(capturedToken.IsCancellationRequested);
            Assert.Contains("個別解説の生成を中止しました", cut.Find("[data-testid=\"batch-explanation-status\"]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Selecting_Unseen_Commit_Does_Not_Render_Drafts_Panel()
    {
        var target = new Commit
        {
            Sha = "aaa1111aaa1111aaa1111aaa1111aaa1111aaa1",
            PrNumber = 61073,
            Message = "Update unread docs",
            Author = "docs-bot",
            AuthoredAt = new DateTime(2026, 5, 15, 2, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 15, 2, 0, 0, DateTimeKind.Utc),
            Review = new Review { Sha = "aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", Status = ReviewStatus.Unseen },
        };

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(CountsFor(ReviewStatus.Unseen)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> commits = filter.Status == ReviewStatus.Unseen ? [target] : [];
                return Task.FromResult(commits);
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out _);
        var cut = ctx.Render<Workbench>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]")));

        cut.Find("[data-testid=\"commit-row\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=\"review-actions\"]"));
            Assert.Empty(cut.FindAll("[data-testid=\"drafts-panel\"]"));
            Assert.True(
                cut.Markup.IndexOf("data-testid=\"review-actions\"", StringComparison.Ordinal)
                < cut.Markup.IndexOf("data-testid=\"commit-detail\"", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Selecting_Adopted_Commit_Renders_Drafts_Panel()
    {
        var target = new Commit
        {
            Sha = "bbb2222bbb2222bbb2222bbb2222bbb2222bbb2",
            PrNumber = 61074,
            Message = "Update adopted docs",
            Author = "docs-bot",
            AuthoredAt = new DateTime(2026, 5, 15, 3, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 15, 3, 0, 0, DateTimeKind.Utc),
            Review = new Review { Sha = "bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", Status = ReviewStatus.Adopted },
        };

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(CountsFor(ReviewStatus.Adopted)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> commits = filter.Status == ReviewStatus.Adopted ? [target] : [];
                return Task.FromResult(commits);
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out _);
        var cut = ctx.Render<Workbench>();
        cut.Find("[data-testid=\"sidebar-item-Adopted\"]").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]")));

        cut.Find("[data-testid=\"commit-row\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=\"drafts-panel\"]"));
            var link = cut.Find("[data-testid=\"drafts-jump-link\"]");
            Assert.Equal("#drafts-panel", link.GetAttribute("href"));
            Assert.Equal("drafts-panel", cut.Find("[data-testid=\"drafts-panel\"]").GetAttribute("id"));
        });
    }

    [Fact]
    public async Task Ingestion_Broadcasts_Grow_Unseen_Count_And_Rows_While_Triage_Is_Running()
    {
        var commits = new List<Commit>();
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(
                commits.Select(static _ => ReviewStatus.Unseen))));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = filter.Status is null or ReviewStatus.Unseen
                    ? commits.OrderByDescending(static commit => commit.AuthoredAt).ToArray()
                    : [];
                return Task.FromResult(visible);
            });

        await using var ctx = CreateWorkbenchTestContext(repo, out var broadcaster);
        var cut = ctx.Render<Workbench>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("0", cut.Find("[data-testid=\"sidebar-count-Unseen\"]").TextContent);
            Assert.Empty(cut.FindAll("[data-testid=\"commit-row\"]"));
        });

        commits.Add(MakeWorkbenchCommit("aaa1111aaa1111aaa1111aaa1111aaa1111aaa1", "first triage commit"));
        broadcaster.Publish();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("1", cut.Find("[data-testid=\"sidebar-count-Unseen\"]").TextContent);
            Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));
        });

        commits.Add(MakeWorkbenchCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", "second triage commit"));
        broadcaster.Publish();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("2", cut.Find("[data-testid=\"sidebar-count-Unseen\"]").TextContent);
            Assert.Equal(2, cut.FindAll("[data-testid=\"commit-row\"]").Count);
        });
    }

    [Theory]
    [InlineData(173)]
    [InlineData(941)]
    [InlineData(20260518)]
    public async Task Workbench_Monkey_Status_Search_And_Move_Operations_Keep_UI_State_Consistent(int seed)
    {
        var random = new Random(seed);
        var commits = Enumerable.Range(0, 10)
            .Select(index => MakeWorkbenchCommit(
                $"{index + 1:x7}{index + 1:x7}{index + 1:x7}{index + 1:x7}{index + 1:x7}{index + 1:x5}",
                $"monkey commit {index + 1}"))
            .ToList();
        var initialStatuses = new[]
        {
            ReviewStatus.Unseen,
            ReviewStatus.Unseen,
            ReviewStatus.Adopted,
            ReviewStatus.Later,
            ReviewStatus.Rejected,
            ReviewStatus.Rejected,
            ReviewStatus.Archived,
            ReviewStatus.Unseen,
            ReviewStatus.Later,
            ReviewStatus.Adopted,
        };
        var statuses = commits
            .Select((commit, index) => new { commit.Sha, Status = initialStatuses[index] })
            .ToDictionary(static item => item.Sha, static item => item.Status, StringComparer.Ordinal);

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(BuildCounts(statuses.Values)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> visible = VisibleShas(commits, statuses, filter.Status, filter.ShaQuery)
                    .Select(sha => CloneWorkbenchCommit(commits.Single(commit => commit.Sha == sha), statuses[sha]))
                    .ToArray();
                return Task.FromResult(visible);
            });
        repo.SetReviewAsync(Arg.Any<string>(), Arg.Any<ReviewStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var sha = call.ArgAt<string>(0);
                if (statuses.ContainsKey(sha))
                {
                    statuses[sha] = call.ArgAt<ReviewStatus>(1);
                }
                return Task.CompletedTask;
            });
        repo.DeleteUnseenCommitsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requestedShas = call.Arg<IEnumerable<string>>().ToArray();
                var deleted = 0;
                foreach (var sha in requestedShas)
                {
                    if (statuses.TryGetValue(sha, out var status) && status == ReviewStatus.Unseen)
                    {
                        statuses.Remove(sha);
                        commits.RemoveAll(commit => commit.Sha == sha);
                        deleted++;
                    }
                }
                return Task.FromResult(deleted);
            });
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([]));

        await using var ctx = CreateWorkbenchTestContext(repo, out var broadcaster);
        var cut = ctx.Render<Workbench>();

        var selectedStatus = ReviewStatus.Unseen;
        var selectedSha = (string?)null;
        var selectedShas = new HashSet<string>(StringComparer.Ordinal);
        var searchQuery = string.Empty;

        cut.WaitForAssertion(() => AssertWorkbenchState(cut, commits, statuses, selectedStatus, searchQuery, selectedSha, selectedShas));

        for (var step = 0; step < 45; step++)
        {
            var visible = VisibleShas(commits, statuses, selectedStatus, searchQuery).ToArray();
            switch (random.Next(8))
            {
                case 0:
                    selectedStatus = PickReviewStatus(random);
                    selectedSha = null;
                    selectedShas.Clear();
                    cut.Find($"[data-testid=\"sidebar-item-{selectedStatus}\"]").Click();
                    break;

                case 1 when commits.Count > 0:
                    var searchTarget = commits[random.Next(commits.Count)];
                    searchQuery = random.Next(3) switch
                    {
                        0 => searchTarget.Sha[..7].ToUpperInvariant(),
                        1 => searchTarget.PrNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _ => searchTarget.Message.Split(' ')[0].ToUpperInvariant(),
                    };
                    selectedSha = null;
                    selectedShas.Clear();
                    cut.Find("[data-testid=\"commit-hash-search\"]").Input(searchQuery);
                    break;

                case 2 when visible.Length > 0:
                    selectedSha = visible[random.Next(visible.Length)];
                    cut.Find($"[data-testid=\"commit-row\"][data-sha=\"{selectedSha}\"]").Click();
                    break;

                case 3 when visible.Length > 0:
                    var checkboxSha = visible[random.Next(visible.Length)];
                    if (!selectedShas.Add(checkboxSha))
                    {
                        selectedShas.Remove(checkboxSha);
                    }
                    cut.Find($"[data-testid=\"commit-select\"][data-sha=\"{checkboxSha}\"]").Change(selectedShas.Contains(checkboxSha));
                    break;

                case 4 when selectedSha is not null:
                    var singleTargetStatus = PickManualReviewStatus(random);
                    statuses[selectedSha] = singleTargetStatus;
                    ClickSingleReviewAction(cut, singleTargetStatus);
                    selectedSha = null;
                    selectedShas.Clear();
                    break;

                case 5 when selectedShas.Count > 0:
                    var bulkTargetStatus = PickBulkMoveStatus(random, selectedStatus);
                    foreach (var sha in selectedShas)
                    {
                        if (statuses.ContainsKey(sha))
                        {
                            statuses[sha] = bulkTargetStatus;
                        }
                    }
                    cut.Find($"[data-testid=\"bulk-review-{bulkTargetStatus.ToString().ToLowerInvariant()}\"]").Click();
                    selectedSha = null;
                    selectedShas.Clear();
                    break;

                case 6 when selectedStatus == ReviewStatus.Unseen && selectedShas.Count > 0:
                    var deletedShas = selectedShas.Where(sha => statuses.TryGetValue(sha, out var status) && status == ReviewStatus.Unseen).ToArray();
                    cut.Find("[data-testid=\"bulk-delete-unseen\"]").Click();
                    foreach (var sha in deletedShas)
                    {
                        statuses.Remove(sha);
                        commits.RemoveAll(commit => commit.Sha == sha);
                    }
                    selectedSha = null;
                    selectedShas.Clear();
                    break;

                default:
                    broadcaster.Publish();
                    selectedShas.Clear();
                    break;
            }

            cut.WaitForAssertion(() => AssertWorkbenchState(cut, commits, statuses, selectedStatus, searchQuery, selectedSha, selectedShas));
        }
    }

    private static Bunit.BunitContext CreateWorkbenchTestContext(
        IRadarRepository repo,
        out ReviewBroadcaster broadcaster,
        IAppUserSettingsStore? settingsStore = null,
        ICopilotAgent? agent = null,
        IClipboard? clipboard = null,
        ReviewBroadcaster? existingBroadcaster = null)
    {
        broadcaster = existingBroadcaster ?? new ReviewBroadcaster();
        var auth = Substitute.For<IGitHubAuthSession>();
        auth.GetStateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GitHubAuthState.SignedIn));
        auth.GetCurrentLoginAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("octocat"));
        var resolver = Substitute.For<IPathToUrlResolver>();
        if (settingsStore is null)
        {
            settingsStore = Substitute.For<IAppUserSettingsStore>();
            settingsStore.Current.Returns(AppUserSettings.Default);
        }

        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services
            .AddSingleton(repo)
            .AddSingleton<IReviewBroadcaster>(broadcaster)
            .AddSingleton(auth)
            .AddSingleton(agent ?? Substitute.For<ICopilotAgent>())
            .AddSingleton(clipboard ?? Substitute.For<IClipboard>())
            .AddSingleton(resolver)
            .AddSingleton<IOptions<DocsApiOptions>>(Options.Create(new DocsApiOptions
            {
                BaseAddress = new Uri("https://docs.github.com/"),
            }))
            .AddSingleton(settingsStore);
        return ctx;
    }

    private static Dictionary<ReviewStatus, int> CountsFor(ReviewStatus currentStatus)
    {
        return new Dictionary<ReviewStatus, int>
        {
            [ReviewStatus.Unseen] = currentStatus == ReviewStatus.Unseen ? 1 : 0,
            [ReviewStatus.Seen] = currentStatus == ReviewStatus.Seen ? 1 : 0,
            [ReviewStatus.Adopted] = currentStatus == ReviewStatus.Adopted ? 1 : 0,
            [ReviewStatus.Rejected] = currentStatus == ReviewStatus.Rejected ? 1 : 0,
            [ReviewStatus.Archived] = currentStatus == ReviewStatus.Archived ? 1 : 0,
            [ReviewStatus.Later] = currentStatus == ReviewStatus.Later ? 1 : 0,
        };
    }

    private static Dictionary<ReviewStatus, int> BuildCounts(IEnumerable<ReviewStatus> statuses)
    {
        var counts = Enum.GetValues<ReviewStatus>()
            .ToDictionary(static status => status, _ => 0);
        foreach (var status in statuses)
        {
            counts[status]++;
        }
        return counts;
    }

    private static Commit MakeWorkbenchCommit(string sha, string message)
        => new()
        {
            Sha = sha,
            PrNumber = 61075,
            Message = message,
            Author = "docs-bot",
            AuthoredAt = new DateTime(2026, 5, 15, 4, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 15, 4, 0, 0, DateTimeKind.Utc),
        };

    private static Commit CloneWorkbenchCommit(Commit source, ReviewStatus status)
        => new()
        {
            Sha = source.Sha,
            PrNumber = source.PrNumber,
            Message = source.Message,
            Author = source.Author,
            AuthoredAt = source.AuthoredAt,
            FetchedAt = source.FetchedAt,
            Review = new Review { Sha = source.Sha, Status = status },
        };

    private static string[] VisibleShas(
        IEnumerable<Commit> commits,
        IReadOnlyDictionary<string, ReviewStatus> statuses,
        ReviewStatus? selectedStatus,
        string? searchQuery)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery.Trim().ToLowerInvariant();
        return commits
            .Where(commit => statuses.TryGetValue(commit.Sha, out var status) && (selectedStatus is null || status == selectedStatus))
            .Where(commit => CommitMatchesSearch(commit, normalizedSearch))
            .Select(static commit => commit.Sha)
            .ToArray();
    }

    private static bool CommitMatchesSearch(Commit commit, string? searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return true;
        }

        var normalized = searchQuery.Trim().ToLowerInvariant();
        var prQuery = normalized.TrimStart('#');
        return commit.Sha.Contains(normalized, StringComparison.Ordinal)
            || commit.Message.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || commit.PrNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) == prQuery;
    }

    private static ReviewStatus PickReviewStatus(Random random)
    {
        var statuses = new[]
        {
            ReviewStatus.Unseen,
            ReviewStatus.Adopted,
            ReviewStatus.Later,
            ReviewStatus.Rejected,
            ReviewStatus.Archived,
        };
        return statuses[random.Next(statuses.Length)];
    }

    private static ReviewStatus PickManualReviewStatus(Random random)
    {
        var statuses = new[]
        {
            ReviewStatus.Adopted,
            ReviewStatus.Later,
            ReviewStatus.Archived,
        };
        return statuses[random.Next(statuses.Length)];
    }

    private static ReviewStatus PickBulkMoveStatus(Random random, ReviewStatus currentStatus)
    {
        var statuses = new[]
        {
            ReviewStatus.Unseen,
            ReviewStatus.Adopted,
            ReviewStatus.Later,
            ReviewStatus.Archived,
        };
        var candidates = statuses.Where(status => status != currentStatus).ToArray();
        return candidates[random.Next(candidates.Length)];
    }

    private static void ClickSingleReviewAction(IRenderedComponent<Workbench> cut, ReviewStatus status)
    {
        switch (status)
        {
            case ReviewStatus.Adopted:
                cut.Find("[data-testid=\"review-adopt\"]").Click();
                break;
            case ReviewStatus.Later:
                cut.Find("[data-testid=\"review-later\"]").Click();
                break;
            case ReviewStatus.Archived:
                cut.Find("[data-testid=\"review-reject-reason\"]").Input("monkey archive");
                cut.Find("[data-testid=\"review-reject\"]").Click();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported manual review status.");
        }
    }

    private static void AssertWorkbenchState(
        IRenderedComponent<Workbench> cut,
        IReadOnlyList<Commit> commits,
        IReadOnlyDictionary<string, ReviewStatus> statuses,
        ReviewStatus selectedStatus,
        string searchQuery,
        string? selectedSha,
        HashSet<string> selectedShas)
    {
        Assert.Contains("active", cut.Find($"[data-testid=\"sidebar-item-{selectedStatus}\"]").ClassList);

        var expectedRows = VisibleShas(commits, statuses, selectedStatus, searchQuery).ToArray();
        var actualRows = cut.FindAll("[data-testid=\"commit-row\"]")
            .Select(row => row.GetAttribute("data-sha"))
            .ToArray();
        Assert.Equal(expectedRows, actualRows);

        var activeRows = cut.FindAll("[data-testid=\"commit-row\"]")
            .Count(row => row.ClassList.Contains("active"));
        Assert.True(activeRows <= 1, $"Expected at most one active commit row, found {activeRows}.");

        if (selectedSha is null || !expectedRows.Contains(selectedSha, StringComparer.Ordinal))
        {
            Assert.NotNull(cut.Find("[data-testid=\"commit-detail-empty\"]"));
            Assert.Empty(cut.FindAll("[data-testid=\"review-actions\"]"));
        }
        else
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=\"review-actions\"]"));
        }

        if (selectedShas.Count > 0)
        {
            Assert.Contains(
                $"{selectedShas.Count} 件選択中",
                cut.Find("[data-testid=\"bulk-review-count\"]").TextContent,
                StringComparison.Ordinal);
        }
    }
}
