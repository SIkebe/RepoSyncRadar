using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="AppHeader"/>. Verifies the three auth states render the
/// right affordances and that the Morning Triage button drives
/// <see cref="ICopilotAgent.RunMorningTriageAsync"/> + republishes through
/// <see cref="IReviewBroadcaster"/> so Sidebar / CommitList refresh themselves.
/// </summary>
public sealed class AppHeaderTests
{
    [Fact]
    public void Renders_SignedIn_State_With_SignOut_And_Enabled_Triage()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        Assert.Equal(
            "SignedIn",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=\"app-header-signout\"]"));
        Assert.Equal(
            "@octocat",
            cut.Find("[data-testid=\"app-header-login\"]").TextContent);
        Assert.False(cut.Find("[data-testid=\"app-header-sync\"]").HasAttribute("disabled"));
    }

    [Fact]
    public void Renders_NotSignedIn_State_With_SignIn_And_Disabled_Triage()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.NotSignedIn));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        Assert.Equal(
            "NotSignedIn",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=\"app-header-signin\"]"));
        Assert.True(cut.Find("[data-testid=\"app-header-sync\"]").HasAttribute("disabled"));
    }

    [Fact]
    public void Renders_NotConfigured_State_With_Hint_And_Disabled_Triage()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.NotConfigured));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        Assert.Equal(
            "NotConfigured",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=\"app-header-config-hint\"]"));
        Assert.True(cut.Find("[data-testid=\"app-header-sync\"]").HasAttribute("disabled"));
    }

    [Fact]
    public void Triage_Button_Calls_Agent_And_Publishes_Broadcaster()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out var agent, out var broadcaster);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(Total: 3, Inserted: 2, Skipped: 1)));

        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        agent.Received(1).RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
        broadcaster.Received(1).Publish();
        Assert.Contains("新規 2", cut.Find("[data-testid=\"app-header-last-sync\"]").TextContent);
    }

    [Fact]
    public void Triage_Progress_Is_Rendered_While_Agent_Is_Running()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var gate = new TaskCompletionSource<IngestionReport>();
        var sp = BuildServices(session, out var agent, out _);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<IProgress<string>?>()?.Report("Copilot が未読コミットをスコアリングしています…");
                return gate.Task;
            });

        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var status = cut.Find("[data-testid=\"app-header-triage-status\"]").TextContent;
            Assert.Contains("スコアリング", status, StringComparison.Ordinal);
            Assert.Contains("経過", status, StringComparison.Ordinal);
        });

        gate.SetResult(new IngestionReport(Total: 1, Inserted: 1, Skipped: 0));
        cut.WaitForAssertion(() =>
            Assert.Contains("Triage 完了", cut.Find("[data-testid=\"app-header-triage-status\"]").TextContent));
    }

    [Fact]
    public void Triage_Failure_Shows_Error_Without_Publishing_Broadcaster()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out var agent, out var broadcaster);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("network down"));

        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        var err = cut.Find("[data-testid=\"app-header-error\"]");
        Assert.Contains("Triage 失敗", err.TextContent);
        Assert.Contains("network down", err.TextContent);
        broadcaster.DidNotReceive().Publish();
    }

    [Fact]
    public void SignIn_Button_Calls_SignInAsync_And_Refreshes_State()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        // First call (initial render) — NotSignedIn. After SignInAsync succeeds the
        // component re-queries state and should observe SignedIn so the UI flips.
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(GitHubAuthState.NotSignedIn),
                Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-signin\"]").Click();

        session.Received(1).SignInAsync(Arg.Any<CancellationToken>());
        Assert.Equal(
            "SignedIn",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.Equal(
            "@octocat",
            cut.Find("[data-testid=\"app-header-login\"]").TextContent);
    }

    [Fact]
    public void Renders_SignedIn_State_Without_Login_When_UserApi_Returns_Null()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        // Still rendered as signed-in (Morning Triage enabled), but no @login chip — the
        // header must not crash just because /user 5xx'd or rate-limited.
        Assert.Equal(
            "SignedIn",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.Empty(cut.FindAll("[data-testid=\"app-header-login\"]"));
        Assert.False(cut.Find("[data-testid=\"app-header-sync\"]").HasAttribute("disabled"));
    }

    [Fact]
    public void Settings_Button_Loads_And_Renders_Ignore_Rules()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([
                new IgnoreRule
                {
                    Pattern = "content/copilot/**",
                    Reason = "ignore-directory",
                    CreatedAt = new DateTime(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc),
                },
                new IgnoreRule
                {
                    Pattern = "data/release-notes/**",
                    Reason = "noisy",
                    CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0, DateTimeKind.Utc),
                },
            ]));

        var sp = BuildServices(session, out _, out _, repo);
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-settings\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=\"settings-panel\"]"));
            var patterns = cut.FindAll("[data-testid=\"settings-ignore-rule-pattern\"]")
                .Select(static node => node.TextContent)
                .ToArray();
            Assert.Equal(["content/copilot/**", "data/release-notes/**"], patterns);
            Assert.Contains("ignore-directory", cut.Find("[data-testid=\"settings-ignore-rule-reason\"]").TextContent);
        });
        repo.Received(1).GetIgnoreRulesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Settings_Shows_Empty_State_When_No_Ignore_Rules_Exist()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([]));

        var sp = BuildServices(session, out _, out _, repo);
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-settings\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(
                "まだ設定されていません",
                cut.Find("[data-testid=\"settings-ignore-rules-empty\"]").TextContent,
                StringComparison.Ordinal);
        });
    }

    private static ServiceProvider BuildServices(
        IGitHubAuthSession session,
        out ICopilotAgent agent,
        out IReviewBroadcaster broadcaster,
        IRadarRepository? repo = null)
    {
        agent = Substitute.For<ICopilotAgent>();
        broadcaster = Substitute.For<IReviewBroadcaster>();
        return new ServiceCollection()
            .AddSingleton(session)
            .AddSingleton(agent)
            .AddSingleton(broadcaster)
            .AddSingleton(repo ?? Substitute.For<IRadarRepository>())
            .BuildServiceProvider();
    }
}
