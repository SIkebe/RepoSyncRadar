using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// Tests for <see cref="PreviewActions"/> and <see cref="PreviewNavigator"/>
/// (IMPLEMENTATION_PLAN.md §Step 19.5). The component invokes <c>IPreviewCoordinator</c>
/// and publishes the resulting URL via <c>IPreviewNavigator</c>; the host WPF window
/// subscribes to that bus and reassigns <c>DocsView.Source</c>.
/// </summary>
public sealed class PreviewActionsTests
{
    [Fact]
    public void PreviewNavigator_Publish_Raises_Event_With_Uri()
    {
        var sut = new PreviewNavigator();
        Uri? captured = null;
        sut.Requested += (_, url) => captured = url;

        sut.Publish(new Uri("http://localhost:4500/en/foo"));

        Assert.Equal(new Uri("http://localhost:4500/en/foo"), captured);
    }

    [Fact]
    public void PreviewNavigator_Publish_With_Null_Throws()
    {
        var sut = new PreviewNavigator();

        Assert.Throws<ArgumentNullException>(() => sut.Publish(null!));
    }

    [Fact]
    public void Click_Publishes_Url_From_Coordinator()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PreparePreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewLink?>(
                new PreviewLink(new Uri("http://localhost:4500/en/foo"), 4500, "C:/wt")));
        var navigator = new PreviewNavigator();
        Uri? capturedNav = null;
        navigator.Requested += (_, url) => capturedNav = url;
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var commit = new Commit
        {
            Sha = "deadbeef",
            PrNumber = 123,
            Message = "msg",
            Author = "alice",
            Files = { new CommitFile { Sha = "deadbeef", Path = "content/foo/bar.md", Status = "modified" } },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));
        cut.Find("[data-testid=\"preview-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(new Uri("http://localhost:4500/en/foo"), capturedNav);
            Assert.Contains("http://localhost:4500/en/foo", cut.Find("[data-testid=\"preview-url\"]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Click_Shows_Worktree_Path_And_Npm_Install_Hint()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PreparePreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewLink?>(
                new PreviewLink(
                    new Uri("http://localhost:4500/en/foo"),
                    4500,
                    @"C:\github\.cache\docs-worktrees\deadbeef")));
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var commit = new Commit
        {
            Sha = "deadbeef",
            PrNumber = 123,
            Message = "msg",
            Author = "alice",
            Files = { new CommitFile { Sha = "deadbeef", Path = "content/foo/bar.md", Status = "modified" } },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));
        cut.Find("[data-testid=\"preview-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var hint = cut.Find("[data-testid=\"preview-worktree\"]");
            Assert.Contains(@"C:\github\.cache\docs-worktrees\deadbeef", hint.TextContent, StringComparison.Ordinal);
            Assert.Contains("npm install", hint.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Click_When_Disabled_Shows_Hint()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PreparePreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewLink?>(null));
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var commit = new Commit
        {
            Sha = "deadbeef",
            PrNumber = 123,
            Message = "msg",
            Author = "alice",
            Files = { new CommitFile { Sha = "deadbeef", Path = "content/foo/bar.md", Status = "modified" } },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));
        cut.Find("[data-testid=\"preview-button\"]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("無効", cut.Find("[data-testid=\"preview-status\"]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Click_When_Coordinator_Throws_Shows_Error()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PreparePreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<PreviewLink?>>(_ => throw new InvalidOperationException("git fetch failed"));
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var commit = new Commit
        {
            Sha = "deadbeef",
            PrNumber = 123,
            Message = "msg",
            Author = "alice",
            Files = { new CommitFile { Sha = "deadbeef", Path = "content/foo/bar.md", Status = "modified" } },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));
        cut.Find("[data-testid=\"preview-button\"]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("git fetch failed", cut.Find("[data-testid=\"preview-status\"]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Click_When_Coordinator_Throws_NonInvalidOperationException_Does_Not_Crash()
    {
        // Regression: the previous implementation only caught InvalidOperationException,
        // so Win32Exception / IOException from Process.Start bubbled up and the WPF
        // host process terminated. The component must swallow any non-cancellation
        // exception and surface its Message in the status.
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PreparePreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<PreviewLink?>>(_ =>
                throw new System.ComponentModel.Win32Exception(2, "指定されたファイルが見つかりません"));
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var commit = new Commit
        {
            Sha = "deadbeef",
            PrNumber = 123,
            Message = "msg",
            Author = "alice",
            Files = { new CommitFile { Sha = "deadbeef", Path = "content/foo/bar.md", Status = "modified" } },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));
        cut.Find("[data-testid=\"preview-button\"]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("見つかりません", cut.Find("[data-testid=\"preview-status\"]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Click_Forwards_Progress_To_Status()
    {
        IProgress<string>? capturedProgress = null;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PreparePreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedProgress = call.ArgAt<IProgress<string>?>(3);
                capturedProgress?.Report("worktree を作成中…");
                return Task.FromResult<PreviewLink?>(
                    new PreviewLink(new Uri("http://localhost:4500/en/foo"), 4500, "C:/wt"));
            });
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var commit = new Commit
        {
            Sha = "deadbeef",
            PrNumber = 123,
            Message = "msg",
            Author = "alice",
            Files = { new CommitFile { Sha = "deadbeef", Path = "content/foo/bar.md", Status = "modified" } },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));
        cut.Find("[data-testid=\"preview-button\"]").Click();

        // The component must hand a non-null IProgress<string> to the coordinator so
        // long-running pipelines can stream "what is happening right now" to the UI.
        cut.WaitForAssertion(() => Assert.NotNull(capturedProgress));
    }

    [Fact]
    public void Click_Cleanup_Calls_Coordinator_And_Shows_Count()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.CleanupCacheAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(3));
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp));
        cut.Find("[data-testid=\"preview-cleanup-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            coordinator.Received(1).CleanupCacheAsync(Arg.Any<CancellationToken>());
            Assert.Contains("3", cut.Find("[data-testid=\"preview-status\"]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Click_Cleanup_When_Coordinator_Throws_Shows_Error_Message()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.CleanupCacheAsync(Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("disk locked"));
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp));
        cut.Find("[data-testid=\"preview-cleanup-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("disk locked", cut.Find("[data-testid=\"preview-status\"]").TextContent, StringComparison.Ordinal);
        });
    }

    private static ServiceProvider BuildServices(IPreviewCoordinator coordinator, IPreviewNavigator navigator)
    {
        return new ServiceCollection()
            .AddSingleton(coordinator)
            .AddSingleton(navigator)
            .BuildServiceProvider();
    }
}
