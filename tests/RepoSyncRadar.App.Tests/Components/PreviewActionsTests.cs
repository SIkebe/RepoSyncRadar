using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Options;
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
    public void PreviewNavigator_PublishComparison_Raises_Event_With_Request()
    {
        var sut = new PreviewNavigator();
        PreviewComparisonRequest? captured = null;
        sut.ComparisonRequested += (_, request) => captured = request;
        var request = new PreviewComparisonRequest(
            new Uri("http://localhost:4501/en/foo"),
            new Uri("http://localhost:4500/en/foo"),
            "変更前",
            "PR HEAD");

        sut.PublishComparison(request);

        Assert.Same(request, captured);
    }

    [Fact]
    public void Click_Publishes_Comparison_From_Coordinator()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewComparisonLink?>(MakeComparisonLink(
                beforeUrl: "http://localhost:4501/en/foo",
                afterUrl: "http://localhost:4500/en/foo")));
        var navigator = new PreviewNavigator();
        PreviewComparisonRequest? captured = null;
        navigator.ComparisonRequested += (_, request) => captured = request;
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
            Assert.Equal("http://localhost:4501/en/foo", captured?.BeforeUrl.AbsoluteUri);
            Assert.Equal("http://localhost:4500/en/foo", captured?.AfterUrl.AbsoluteUri);
            Assert.Equal("content/foo/bar.md", captured?.FilePath);
            Assert.Equal(1, captured?.FileOrdinal);
            Assert.Equal(1, captured?.FileCount);
            Assert.Contains("変更前", captured?.BeforeLabel, StringComparison.Ordinal);
            Assert.Contains("http://localhost:4500/en/foo", cut.Find("[data-testid=\"preview-url\"]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Click_Uses_First_Mappable_Content_File_For_Local_Preview()
    {
        string? capturedPath = null;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedPath = call.ArgAt<string?>(2);
                return Task.FromResult<PreviewComparisonLink?>(MakeComparisonLink(
                    beforeUrl: "http://localhost:4501/en/copilot/about-copilot",
                    afterUrl: "http://localhost:4500/en/copilot/about-copilot"));
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
            Files =
            {
                new CommitFile { Sha = "deadbeef", Path = "data/ui.yml", Status = "modified" },
                new CommitFile { Sha = "deadbeef", Path = "content/copilot/about-copilot.md", Status = "modified" },
            },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));

        cut.Find("[data-testid=\"preview-button\"]").Click();

        cut.WaitForAssertion(() =>
            Assert.Equal("content/copilot/about-copilot.md", capturedPath));
    }

    [Fact]
    public void File_Switcher_Opens_Selected_File_Comparison()
    {
        string? capturedPath = null;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedPath = call.ArgAt<string?>(2);
                return Task.FromResult<PreviewComparisonLink?>(MakeComparisonLink(
                    beforeUrl: "http://localhost:4501/en/second",
                    afterUrl: "http://localhost:4500/en/second"));
            });
        var navigator = new PreviewNavigator();
        PreviewComparisonRequest? captured = null;
        navigator.ComparisonRequested += (_, request) => captured = request;
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var commit = new Commit
        {
            Sha = "deadbeef",
            PrNumber = 123,
            Message = "msg",
            Author = "alice",
            Files =
            {
                new CommitFile { Sha = "deadbeef", Path = "content/copilot/first.md", Status = "modified" },
                new CommitFile { Sha = "deadbeef", Path = "src/schema.json", Status = "modified" },
                new CommitFile { Sha = "deadbeef", Path = "content/copilot/second.md", Status = "modified" },
            },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));

        var switchButtons = cut.FindAll("[data-testid=\"preview-file-switch\"]");
        Assert.Equal(2, switchButtons.Count);
        Assert.Contains("1/2", switchButtons[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("2/2", switchButtons[1].TextContent, StringComparison.Ordinal);

        switchButtons[1].Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("content/copilot/second.md", capturedPath);
            Assert.Equal("content/copilot/second.md", captured?.FilePath);
            Assert.Equal(2, captured?.FileOrdinal);
            Assert.Equal(2, captured?.FileCount);
            Assert.Contains("active", cut.Find("[data-path=\"content/copilot/second.md\"]").ClassList);
        });
    }

    [Fact]
    public void Click_When_No_Mappable_Content_File_Does_Not_Start_Preview()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var commit = new Commit
        {
            Sha = "deadbeef",
            PrNumber = 123,
            Message = "GraphQL schema update",
            Author = "docs-bot",
            Files =
            {
                new CommitFile { Sha = "deadbeef", Path = "src/graphql/data/fpt/schema.docs.graphql", Status = "modified" },
                new CommitFile { Sha = "deadbeef", Path = "src/graphql/data/fpt/schema.json", Status = "modified" },
            },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));

        cut.Find("[data-testid=\"preview-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("公開ドキュメント記事または Markdown", cut.Find("[data-testid=\"preview-status\"]").TextContent, StringComparison.Ordinal);
            _ = coordinator.DidNotReceive().PreparePreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>());
            _ = coordinator.DidNotReceive().PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public void Click_When_No_Content_File_Publishes_Markdown_Comparison()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewComparisonLink?>(new PreviewComparisonLink(
                new Uri("http://127.0.0.1:4500/markdown/before"),
                new Uri("http://127.0.0.1:4500/markdown/after"),
                4500,
                4500,
                "C:/wt-before",
                "C:/wt-after",
                "parent123456",
                "deadbeef")));
        var navigator = new PreviewNavigator();
        PreviewComparisonRequest? captured = null;
        navigator.ComparisonRequested += (_, request) => captured = request;
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var commit = new Commit
        {
            Sha = "deadbeef",
            PrNumber = 123,
            Message = "Update changelog",
            Author = "alice",
            Files = { new CommitFile { Sha = "deadbeef", Path = "CHANGELOG.md", Status = "modified" } },
        };
        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Commit, commit));
        cut.Find("[data-testid=\"preview-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("http://127.0.0.1:4500/markdown/before", captured?.BeforeUrl.AbsoluteUri);
            Assert.Equal("http://127.0.0.1:4500/markdown/after", captured?.AfterUrl.AbsoluteUri);
            Assert.Equal("CHANGELOG.md", captured?.FilePath);
            Assert.Contains("Markdown", captured?.BeforeLabel, StringComparison.Ordinal);
            Assert.Contains("Markdown", cut.Find("[data-testid=\"preview-status\"]").TextContent, StringComparison.Ordinal);
        });
        _ = coordinator.DidNotReceive().PreparePreviewAsync(
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<IProgress<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Click_Shows_Worktree_Path_And_Automatic_Npm_Install_Hint()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewComparisonLink?>(MakeComparisonLink(
                beforeUrl: "http://localhost:4501/en/foo",
                afterUrl: "http://localhost:4500/en/foo",
                afterWorktreePath: @"C:\github\.cache\docs-worktrees\deadbeef")));
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
            Assert.Contains("自動実行", hint.TextContent, StringComparison.Ordinal);
            Assert.Contains("npm install", hint.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Click_When_Disabled_Shows_Hint()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewComparisonLink?>(null));
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
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<PreviewComparisonLink?>>(_ => throw new InvalidOperationException("git fetch failed"));
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
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<PreviewComparisonLink?>>(_ =>
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
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedProgress = call.ArgAt<IProgress<string>?>(3);
                capturedProgress?.Report("worktree を作成中…");
                return Task.FromResult<PreviewComparisonLink?>(MakeComparisonLink());
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
    public void Click_Shows_Progress_Ui_With_Spinner_And_Cancel_Button_While_Coordinator_Is_Running()
    {
        // P1-A/B/F: while the comparison preview is still pending, the UI must
        // show the progress card (spinner + 経過秒 + 中止 button + log tail
        // container), not the silent "起動中…" button text.
        var tcs = new TaskCompletionSource<PreviewComparisonLink?>();
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => tcs.Task);
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
            cut.Find("[data-testid=\"preview-progress\"]");
            cut.Find("[data-testid=\"preview-cancel-button\"]");
            var elapsed = cut.Find("[data-testid=\"preview-progress-elapsed\"]").TextContent;
            Assert.Contains("経過", elapsed, StringComparison.Ordinal);
            Assert.Contains("上限", elapsed, StringComparison.Ordinal);
        });

        // Release the coordinator so the test's finalizer is not blocked on the pending task.
        tcs.SetResult(MakeComparisonLink());
    }

    [Fact]
    public void Click_Cancel_Aborts_Coordinator_And_Shows_Cancelled_Status()
    {
        // P1-F: pressing 中止 must propagate cancellation to the coordinator
        // (so PreviewServerHost can kill the npm child) and surface a "中止しました"
        // status — not raise the OperationCanceledException into the host.
        var tcs = new TaskCompletionSource<PreviewComparisonLink?>();
        CancellationToken receivedToken = default;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                receivedToken = call.ArgAt<CancellationToken>(4);
                receivedToken.Register(() =>
                    tcs.TrySetException(new OperationCanceledException(receivedToken)));
                return tcs.Task;
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
        cut.WaitForAssertion(() => cut.Find("[data-testid=\"preview-cancel-button\"]"));

        cut.Find("[data-testid=\"preview-cancel-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(receivedToken.IsCancellationRequested);
            var status = cut.Find("[data-testid=\"preview-status\"]").TextContent;
            Assert.Contains("中止", status, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Click_Times_Out_When_Coordinator_Exceeds_Preview_Limit()
    {
        var tcs = new TaskCompletionSource<PreviewComparisonLink?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                receivedToken = call.ArgAt<CancellationToken>(4);
                receivedToken.Register(() =>
                    tcs.TrySetException(new OperationCanceledException(receivedToken)));
                return tcs.Task;
            });
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator, previewReadyTimeoutSeconds: 1);
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
            Assert.True(receivedToken.IsCancellationRequested);
            var status = cut.Find("[data-testid=\"preview-status\"]").TextContent;
            Assert.Contains("上限 1 秒", status, StringComparison.Ordinal);
        }, timeout: TimeSpan.FromSeconds(3));
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

    [Fact]
    public void Click_Cleanup_Shows_Progress_And_Advances_Elapsed_While_Running()
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.CleanupCacheAsync(Arg.Any<CancellationToken>())
            .Returns(_ => tcs.Task);
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp));
        cut.Find("[data-testid=\"preview-cleanup-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=\"preview-progress\"]");
            cut.Find("[data-testid=\"preview-cancel-button\"]");
            Assert.Contains(
                "キャッシュをクリーンアップ中",
                cut.Find("[data-testid=\"preview-progress-text\"]").TextContent,
                StringComparison.Ordinal);
        });
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(
                "1 秒経過",
                cut.Find("[data-testid=\"preview-progress-elapsed\"]").TextContent,
                StringComparison.Ordinal);
        }, timeout: TimeSpan.FromSeconds(3));

        tcs.SetResult(2);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("2 件", cut.Find("[data-testid=\"preview-status\"]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Click_Cleanup_Times_Out_When_Coordinator_Exceeds_Limit()
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.CleanupCacheAsync(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                receivedToken = call.ArgAt<CancellationToken>(0);
                receivedToken.Register(() =>
                    tcs.TrySetException(new OperationCanceledException(receivedToken)));
                return tcs.Task;
            });
        var navigator = new PreviewNavigator();
        var sp = BuildServices(coordinator, navigator, previewReadyTimeoutSeconds: 1);
        using var ctx = new Bunit.TestContext();

        var cut = ctx.RenderComponent<PreviewActions>(p => p
            .AddCascadingValue<IServiceProvider>(sp));
        cut.Find("[data-testid=\"preview-cleanup-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(receivedToken.IsCancellationRequested);
            var status = cut.Find("[data-testid=\"preview-status\"]").TextContent;
            Assert.Contains("キャッシュ削除が上限 1 秒", status, StringComparison.Ordinal);
        }, timeout: TimeSpan.FromSeconds(3));
    }

    private static ServiceProvider BuildServices(
        IPreviewCoordinator coordinator,
        IPreviewNavigator navigator,
        int previewReadyTimeoutSeconds = 600)
    {
        // PreviewActions now also resolves IOptions<DocsRepositoryOptions> (for the
        // P1 elapsed-vs-timeout label) and PreviewServerHost (for the live log tail
        // it polls every 500 ms during startup). The fakes return empty data, which
        // is what the test expects: no preview is actually running.
        var options = Options.Create(new DocsRepositoryOptions
        {
            PreviewReadyTimeoutSeconds = previewReadyTimeoutSeconds,
        });
        return new ServiceCollection()
            .AddSingleton(coordinator)
            .AddSingleton(navigator)
            .AddSingleton(options)
            .AddSingleton(Substitute.For<IProcessRunner>())
            .AddSingleton(Substitute.For<IPortReadyProbe>())
            .AddLogging()
            .AddSingleton<PreviewServerHost>()
            .BuildServiceProvider();
    }

    private static PreviewComparisonLink MakeComparisonLink(
        string beforeUrl = "http://localhost:4501/en/foo",
        string afterUrl = "http://localhost:4500/en/foo",
        string beforeWorktreePath = "C:/wt-before",
        string afterWorktreePath = "C:/wt-after")
        => new(
            new Uri(beforeUrl),
            new Uri(afterUrl),
            4501,
            4500,
            beforeWorktreePath,
            afterWorktreePath,
            "parent123456",
            "deadbeef");
}
