using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="AppHeader"/>. Verifies the three auth states render the
/// right affordances and that the Sync button drives
/// <see cref="ICommitIngestionService.IngestAsync"/> + republishes through
/// <see cref="IReviewBroadcaster"/> so Sidebar / CommitList refresh themselves.
/// </summary>
public sealed class AppHeaderTests
{
    [Fact]
    public void Renders_SignedIn_State_With_SignOut_And_Enabled_Sync()
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
    public void Renders_NotSignedIn_State_With_SignIn_And_Disabled_Sync()
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
    public void Renders_NotConfigured_State_With_Hint_And_Disabled_Sync()
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
    public void Sync_Button_Calls_IngestAsync_And_Publishes_Broadcaster()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out var ingestion, out var broadcaster);
        ingestion
            .IngestAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(Total: 3, Inserted: 2, Skipped: 1)));

        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        ingestion.Received(1).IngestAsync(Arg.Any<CancellationToken>());
        broadcaster.Received(1).Publish();
        Assert.Contains("新規 2", cut.Find("[data-testid=\"app-header-last-sync\"]").TextContent);
    }

    [Fact]
    public void Sync_Failure_Shows_Error_Without_Publishing_Broadcaster()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out var ingestion, out var broadcaster);
        ingestion
            .IngestAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("network down"));

        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        var err = cut.Find("[data-testid=\"app-header-error\"]");
        Assert.Contains("Sync 失敗", err.TextContent);
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

        // Still rendered as signed-in (Sync enabled), but no @login chip — the
        // header must not crash just because /user 5xx'd or rate-limited.
        Assert.Equal(
            "SignedIn",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.Empty(cut.FindAll("[data-testid=\"app-header-login\"]"));
        Assert.False(cut.Find("[data-testid=\"app-header-sync\"]").HasAttribute("disabled"));
    }

    private static ServiceProvider BuildServices(
        IGitHubAuthSession session,
        out ICommitIngestionService ingestion,
        out IReviewBroadcaster broadcaster)
    {
        ingestion = Substitute.For<ICommitIngestionService>();
        broadcaster = Substitute.For<IReviewBroadcaster>();
        return new ServiceCollection()
            .AddSingleton(session)
            .AddSingleton(ingestion)
            .AddSingleton(broadcaster)
            .BuildServiceProvider();
    }
}
