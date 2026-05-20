using System.Formats.Tar;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Integrations.Tests.Preview;

/// <summary>
/// Tests for <see cref="PreviewCoordinator"/> (IMPLEMENTATION_PLAN.md §Step 19.5). Wires
/// the real <see cref="DocsWorktreeManager"/> + <see cref="PreviewServerHost"/> +
/// <see cref="PreviewSession"/> with a substitute <see cref="IProcessRunner"/> so the
/// orchestration sequence and idempotency around the cached SHA can be asserted
/// without spawning real <c>git</c> or <c>npm</c> processes.
/// </summary>
public sealed class PreviewCoordinatorTests : IDisposable
{
    private static readonly int[] ComparisonPorts = [4500, 4501];

    private readonly string _tempRoot;

    public PreviewCoordinatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "rsr-preview-coord-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task PreparePreviewAsync_When_Disabled_Returns_Null()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var sut = BuildSut(runner, bareCloneDir: string.Empty, cloneUrl: string.Empty);

        var link = await sut.PreparePreviewAsync(123, "deadbeefcafe", "content/foo/bar.md", cancellationToken: ct);

        Assert.Null(link);
        await runner.DidNotReceiveWithAnyArgs()
            .RunAsync(default!, default!, default!, Arg.Any<CancellationToken>());
        runner.DidNotReceiveWithAnyArgs().Start(default!, default!, default!, default);
    }

    [Fact]
    public async Task PreparePreviewAsync_Runs_Steps_In_Order()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees");
        var runner = Substitute.For<IProcessRunner>();
        var calls = new List<string>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"RUN {call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        runner.RunAsync("git", "cat-file -e deadbeefcafe^{commit}", bare, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"RUN {call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(128, string.Empty, "missing"));
            });
        var handle = Substitute.For<IProcessHandle>();
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>())
            .Returns(call =>
            {
                calls.Add($"START {call.ArgAt<string>(0)} {call.ArgAt<string>(1)} cwd={call.ArgAt<string>(2)}");
                return handle;
            });
        var session = new PreviewSession();
        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            session: session);

        var link = await sut.PreparePreviewAsync(123, "deadbeefcafe", "content/foo/bar.md", cancellationToken: ct);

        Assert.NotNull(link);
        Assert.Equal(4500, link!.Port);
        Assert.Equal(new Uri("http://localhost:4500/en/foo/bar"), link.Url);
        Assert.Equal(5, calls.Count);
        Assert.StartsWith("RUN git clone --bare", calls[0], StringComparison.Ordinal);
        Assert.StartsWith("RUN git cat-file -e deadbeefcafe^{commit}", calls[1], StringComparison.Ordinal);
        Assert.StartsWith("RUN git fetch origin +refs/pull/123/head:refs/pull/123/head", calls[2], StringComparison.Ordinal);
        Assert.StartsWith("RUN git worktree add", calls[3], StringComparison.Ordinal);
        Assert.StartsWith("START npm run dev -- --port 4500", calls[4], StringComparison.Ordinal);
        Assert.True(session.IsActive);
        Assert.Equal(4500, session.ActivePort);
    }

    [Fact]
    public async Task PrepareComparisonPreviewAsync_Uses_Parent_And_Head_Local_Servers()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-compare");
        var runner = Substitute.For<IProcessRunner>();
        var calls = new List<string>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"RUN {call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"RUN {call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty));
            });
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>())
            .Returns(call =>
            {
                calls.Add($"START {call.ArgAt<string>(0)} {call.ArgAt<string>(1)} cwd={call.ArgAt<string>(2)}");
                var handle = Substitute.For<IProcessHandle>();
                handle.HasExited.Returns(false);
                return handle;
            });
        var session = new PreviewSession();
        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            session: session);

        var link = await sut.PrepareComparisonPreviewAsync(123, "headsha", "content/foo/bar.md", cancellationToken: ct);

        Assert.NotNull(link);
        Assert.Equal(new Uri("http://localhost:4501/en/foo/bar"), link!.BeforeUrl);
        Assert.Equal(new Uri("http://localhost:4500/en/foo/bar"), link.AfterUrl);
        Assert.Equal("parentsha", link.BeforeSha);
        Assert.Equal("headsha", link.AfterSha);
        Assert.True(session.IsAllowed(link.BeforeUrl));
        Assert.True(session.IsAllowed(link.AfterUrl));
        Assert.Equal(ComparisonPorts, session.ActivePorts);
        Assert.Contains(calls, c => c.StartsWith("RUN git rev-parse headsha^", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.StartsWith("START npm run dev -- --port 4501", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.StartsWith("START npm run dev -- --port 4500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareComparisonPreviewAsync_Starts_Before_And_After_Servers_In_Parallel()
    {
        // Regression guard for the bottleneck where before-server cold start
        // blocked after-server start, doubling total wait time. Both StartAsync
        // invocations must reach the readiness probe concurrently — the probe
        // uses a Barrier so a sequential implementation deadlocks and the test
        // fails fast.
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-parallel");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "cat-file -e headsha^{commit}", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(128, string.Empty, "missing")));
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty)));
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>())
            .Returns(_ =>
            {
                var handle = Substitute.For<IProcessHandle>();
                handle.HasExited.Returns(false);
                return handle;
            });

        using var barrier = new Barrier(participantCount: 2);
        var probe = Substitute.For<IPortReadyProbe>();
        probe.WaitForListenAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(),
                Arg.Any<Func<bool>?>(), Arg.Any<CancellationToken>())
            // Run the barrier on a thread-pool thread so the synchronous portion
            // of PreviewServerHost.StartAsync can return Task and the caller can
            // begin the *other* server's StartAsync. SignalAndWait blocks the
            // worker thread until both servers signal; if the implementation is
            // sequential the second signal never arrives, the 5-second timeout
            // expires, the lambda returns false, and StartAsync throws — making
            // the test fail fast instead of hanging.
            .Returns(_ => Task.Run(() => barrier.SignalAndWait(TimeSpan.FromSeconds(5))));

        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            probe: probe);

        var link = await sut.PrepareComparisonPreviewAsync(123, "headsha", "content/foo/bar.md", cancellationToken: ct);

        Assert.NotNull(link);
    }

    [Fact]
    public async Task PrepareMarkdownComparisonPreviewAsync_Renders_Markdown_Without_Npm_Server()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-markdown");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "cat-file -e headsha^{commit}", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(128, string.Empty, "missing")));
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty)));
        var capturedPages = new Dictionary<string, string>(StringComparer.Ordinal);
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                foreach (var page in call.ArgAt<IReadOnlyDictionary<string, string>>(1))
                {
                    capturedPages[page.Key] = page.Value;
                }
                contentServer.IsRunning.Returns(true);
                contentServer.CurrentPort.Returns(call.ArgAt<int>(0));
                return Task.CompletedTask;
            });
        var session = new PreviewSession();
        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            session: session,
            contentServer: contentServer,
            onWorktreeAdd: (path, sha) =>
            {
                var markdown = string.Equals(sha, "parentsha", StringComparison.Ordinal)
                    ? "# Changelog\n\nOld entry"
                    : "# Changelog\n\nNew entry";
                File.WriteAllText(Path.Combine(path, "CHANGELOG.md"), markdown);
            });

        var link = await sut.PrepareMarkdownComparisonPreviewAsync(123, "headsha", "CHANGELOG.md", cancellationToken: ct);

        Assert.NotNull(link);
        // §Step 19.9/19.10: version slug と file path を URL に必ず埋め込む。
        // WebView2 の Source 等価判定で「同じ URL」とみなされて navigation が
        // スキップされ、オーバーレイが「変更前ページを準備中…」のまま固まる回帰を防ぐ。
        Assert.Equal(new Uri("http://127.0.0.1:4500/markdown/before?v=fpt&file=CHANGELOG.md"), link!.BeforeUrl);
        Assert.Equal(new Uri("http://127.0.0.1:4500/markdown/after?v=fpt&file=CHANGELOG.md"), link.AfterUrl);
        Assert.True(session.IsAllowed(link.BeforeUrl));
        Assert.True(session.IsAllowed(link.AfterUrl));
        Assert.Contains("Old entry", capturedPages["/markdown/before"], StringComparison.Ordinal);
        Assert.Contains("New entry", capturedPages["/markdown/after"], StringComparison.Ordinal);
        runner.DidNotReceiveWithAnyArgs().Start(default!, default!, default!, default);
        await contentServer.Received(1).StartAsync(
            4500,
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(roots => roots.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareMarkdownComparisonPreviewAsync_Materializes_Local_Image_Assets_From_BareClone()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-markdown-assets.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-markdown-assets");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty)));
        var capturedPages = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string>? capturedAssetRoots = null;
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                foreach (var page in call.ArgAt<IReadOnlyDictionary<string, string>>(1))
                {
                    capturedPages[page.Key] = page.Value;
                }
                capturedAssetRoots = new Dictionary<string, string>(
                    call.ArgAt<IReadOnlyDictionary<string, string>>(2),
                    StringComparer.Ordinal);
                contentServer.IsRunning.Returns(true);
                contentServer.CurrentPort.Returns(call.ArgAt<int>(0));
                return Task.CompletedTask;
            });
        var sourcePath = "content/copilot/how-tos/use-copilot-agents/copilot-memory.md";
        var sut = BuildSut(
            runner,
            bare,
            "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            contentServer: contentServer,
            onWorktreeAdd: (path, _) =>
            {
                var contentDir = Path.Combine(path, "content", "copilot", "how-tos", "use-copilot-agents");
                Directory.CreateDirectory(Path.Combine(contentDir, "images"));
                Directory.CreateDirectory(Path.Combine(path, "assets", "images", "help", "copilot"));
                File.WriteAllText(
                    Path.Combine(contentDir, "copilot-memory.md"),
                    "![User memory](/assets/images/help/copilot/copilot-user-memory-list.png)\n\n![Local diagram](<images/local diagram.png>)");
                File.WriteAllBytes(Path.Combine(path, "assets", "images", "help", "copilot", "copilot-user-memory-list.png"), [0x89, 0x50, 0x4e, 0x47]);
                File.WriteAllBytes(Path.Combine(contentDir, "images", "local diagram.png"), [0x4c, 0x4f, 0x43, 0x41, 0x4c]);
            });

        var link = await sut.PrepareMarkdownComparisonPreviewAsync(123, "headsha", sourcePath, cancellationToken: ct);

        Assert.NotNull(link);
        Assert.NotNull(capturedAssetRoots);
        Assert.True(capturedAssetRoots!.TryGetValue("/markdown-assets/before", out var beforeAssetRoot));
        Assert.True(capturedAssetRoots.TryGetValue("/markdown-assets/after", out var afterAssetRoot));
        Assert.Contains("src=\"/markdown-assets/after/assets/images/help/copilot/copilot-user-memory-list.png\"", capturedPages["/markdown/after"], StringComparison.Ordinal);
        Assert.Contains("src=\"/markdown-assets/after/content/copilot/how-tos/use-copilot-agents/images/local%20diagram.png\"", capturedPages["/markdown/after"], StringComparison.Ordinal);
        Assert.Equal([0x89, 0x50, 0x4e, 0x47], await File.ReadAllBytesAsync(Path.Combine(afterAssetRoot!, "assets", "images", "help", "copilot", "copilot-user-memory-list.png"), ct));
        Assert.Equal([0x4c, 0x4f, 0x43, 0x41, 0x4c], await File.ReadAllBytesAsync(Path.Combine(beforeAssetRoot!, "content", "copilot", "how-tos", "use-copilot-agents", "images", "local diagram.png"), ct));
        runner.DidNotReceiveWithAnyArgs().Start(default!, default!, default!, default);
    }

    [Fact]
    public async Task PrepareMarkdownComparisonPreviewAsync_Reports_Detailed_Preparation_Progress()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-progress.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-markdown-progress");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "cat-file -e headsha^{commit}", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(128, string.Empty, "missing")));
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty)));
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            contentServer: contentServer,
            onWorktreeAdd: (path, _) => File.WriteAllText(Path.Combine(path, "CHANGELOG.md"), "# Changelog\n\nEntry"));
        var progress = new ListProgress();

        var link = await sut.PrepareMarkdownComparisonPreviewAsync(123, "headsha", "CHANGELOG.md", progress, cancellationToken: ct);

        Assert.NotNull(link);
        Assert.Contains(progress.Items, m => m.Contains("準備済みデータ", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("git fetch", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("親コミット", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("bare clone", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("変更前 Markdown", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("PR HEAD Markdown", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("フロントマター", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("HTML に変換", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("Markdown 比較プレビューを起動中", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareMarkdownComparisonPreviewAsync_Distinguishes_Added_Metadata_Only_File_From_Missing_File()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-added-metadata.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-added-metadata");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty)));
        var capturedPages = new Dictionary<string, string>(StringComparer.Ordinal);
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                foreach (var page in call.ArgAt<IReadOnlyDictionary<string, string>>(1))
                {
                    capturedPages[page.Key] = page.Value;
                }
                contentServer.IsRunning.Returns(true);
                contentServer.CurrentPort.Returns(call.ArgAt<int>(0));
                return Task.CompletedTask;
            });
        const string filePath = "content/rest/copilot/copilot-cloud-agent-management.md";
        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            contentServer: contentServer,
            onWorktreeAdd: (path, sha) =>
            {
                if (!string.Equals(sha, "headsha", StringComparison.Ordinal))
                {
                    return;
                }

                var fullPath = Path.Combine(path, "content", "rest", "copilot", "copilot-cloud-agent-management.md");
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, """
                    ---
                    title: REST API endpoints for Copilot cloud agent repository management
                    shortTitle: Cloud agent repository management
                    ---

                    <!-- Content after this section is automatically generated -->
                    """);
            });

        var link = await sut.PrepareMarkdownComparisonPreviewAsync(123, "headsha", filePath, cancellationToken: ct);

        Assert.NotNull(link);
        Assert.Equal(2, link!.SourceChangeCount);
        Assert.Contains("この時点にはファイルがありません", capturedPages["/markdown/before"], StringComparison.Ordinal);
        Assert.Contains("REST API endpoints for Copilot cloud agent repository management", capturedPages["/markdown/after"], StringComparison.Ordinal);
        Assert.Contains("data-testid=\"rsr-frontmatter-diff\"", capturedPages["/markdown/after"], StringComparison.Ordinal);
        Assert.Contains("このファイルは存在しますが、本文はありません", capturedPages["/markdown/after"], StringComparison.Ordinal);
        Assert.DoesNotContain("この時点にはファイルがありません", capturedPages["/markdown/after"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareMarkdownComparisonPreviewAsync_Rewrites_Autotitle_From_Worktree_Page_Titles()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-autotitle.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-markdown-autotitle");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty)));
        var capturedPages = new Dictionary<string, string>(StringComparer.Ordinal);
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                foreach (var page in call.ArgAt<IReadOnlyDictionary<string, string>>(1))
                {
                    capturedPages[page.Key] = page.Value;
                }
                contentServer.IsRunning.Returns(true);
                contentServer.CurrentPort.Returns(call.ArgAt<int>(0));
                return Task.CompletedTask;
            });
        var sourcePath = "content/code-security/how-tos/secure-at-scale/apply-security-configuration.md";
        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            contentServer: contentServer,
            onWorktreeAdd: (path, sha) =>
            {
                var contentRoot = Path.Combine(path, "content", "code-security", "how-tos", "secure-at-scale");
                Directory.CreateDirectory(contentRoot);
                var targetTitle = string.Equals(sha, "parentsha", StringComparison.Ordinal)
                    ? "Configuring organization security"
                    : "Applying security configurations in your organization";
                File.WriteAllText(
                    Path.Combine(contentRoot, "configure-organization-security.md"),
                    $"---\ntitle: {targetTitle}\n---\n\nTarget page");
                File.WriteAllText(
                    Path.Combine(contentRoot, "apply-security-configuration.md"),
                    "---\ntitle: Source\n---\n\nSee [AUTOTITLE](/code-security/how-tos/secure-at-scale/configure-organization-security).");
            });

        var link = await sut.PrepareMarkdownComparisonPreviewAsync(123, "headsha", sourcePath, cancellationToken: ct);

        Assert.NotNull(link);
        Assert.Contains(">Configuring organization security</a>", capturedPages["/markdown/before"], StringComparison.Ordinal);
        Assert.Contains(">Applying security configurations in your organization</a>", capturedPages["/markdown/after"], StringComparison.Ordinal);
        Assert.DoesNotContain(">AUTOTITLE</a>", capturedPages["/markdown/after"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareMarkdownComparisonPreviewAsync_Rewrites_Autotitle_From_Redirect_From_Alias()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-autotitle-redirect.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-markdown-autotitle-redirect");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty)));
        var capturedPages = new Dictionary<string, string>(StringComparer.Ordinal);
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                foreach (var page in call.ArgAt<IReadOnlyDictionary<string, string>>(1))
                {
                    capturedPages[page.Key] = page.Value;
                }
                contentServer.IsRunning.Returns(true);
                contentServer.CurrentPort.Returns(call.ArgAt<int>(0));
                return Task.CompletedTask;
            });
        var sourcePath = "content/code-security/concepts/security-at-scale/about-enabling-security-features-at-scale.md";
        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            contentServer: contentServer,
            onWorktreeAdd: (path, _) =>
            {
                var sourceRoot = Path.Combine(path, "content", "code-security", "concepts", "security-at-scale");
                Directory.CreateDirectory(sourceRoot);
                File.WriteAllText(
                    Path.Combine(sourceRoot, "about-enabling-security-features-at-scale.md"),
                    "---\ntitle: Source\n---\n\nFor information, see [AUTOTITLE](/code-security/securing-your-organization/enabling-security-features-in-your-organization/giving-org-access-private-registries).");

                var targetRoot = Path.Combine(path, "content", "code-security", "how-tos", "secure-at-scale", "configure-organization-security", "manage-access");
                Directory.CreateDirectory(targetRoot);
                File.WriteAllText(
                    Path.Combine(targetRoot, "giving-org-access-private-registries.md"),
                    "---\ntitle: Giving private registries access at the organization level\nredirect_from:\n  - /code-security/securing-your-organization/enabling-security-features-in-your-organization/giving-org-access-private-registries\n---\n\nTarget page");
            });

        var link = await sut.PrepareMarkdownComparisonPreviewAsync(123, "headsha", sourcePath, cancellationToken: ct);

        Assert.NotNull(link);
        Assert.Contains(">Giving private registries access at the organization level</a>", capturedPages["/markdown/after"], StringComparison.Ordinal);
        Assert.DoesNotContain(">AUTOTITLE</a>", capturedPages["/markdown/after"], StringComparison.Ordinal);
    }

    // §Step 19.9 regression: docs version ドロップダウンで Free / Enterprise Cloud /
    // Enterprise Server を切り替えると、PreviewCoordinator は同じポートで /markdown/before
    // の内容を差し替える。URL に version 識別子が入っていないと WebView2 は
    // 「Source が変わっていない」と判定して navigation を発火しないため、ホスト側の
    // 「変更前ページを準備中…」オーバーレイが解除されず固まる。BeforeUrl/AfterUrl が
    // version ごとに別 URL になることを保証する。
    [Theory]
    [InlineData(null, "fpt")]
    [InlineData("fpt", "fpt")]
    [InlineData("ghec", "ghec")]
    public async Task PrepareMarkdownComparisonPreviewAsync_Embeds_Version_Slug_In_Url(string? slug, string expectedSlug)
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-" + (slug ?? "default") + ".git");
        var wtRoot = Path.Combine(_tempRoot, "wt-" + (slug ?? "default"));
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty)));
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            contentServer: contentServer,
            onWorktreeAdd: (path, _) =>
            {
                File.WriteAllText(Path.Combine(path, "CHANGELOG.md"), "# Changelog\n\nEntry");
            });

        var version = slug switch
        {
            null => null,
            "fpt" => DocsVersion.Fpt,
            "ghec" => DocsVersion.Ghec,
            _ => throw new InvalidOperationException("unexpected slug"),
        };

        var link = await sut.PrepareMarkdownComparisonPreviewAsync(
            123,
            "headsha",
            "CHANGELOG.md",
            version: version,
            cancellationToken: ct);

        Assert.NotNull(link);
        Assert.Equal(new Uri($"http://127.0.0.1:4500/markdown/before?v={expectedSlug}&file=CHANGELOG.md"), link!.BeforeUrl);
        Assert.Equal(new Uri($"http://127.0.0.1:4500/markdown/after?v={expectedSlug}&file=CHANGELOG.md"), link.AfterUrl);
    }

    // §Step 19.9 regression: 同一ファイル・同一 sha でも version を切り替えれば
    // BeforeUrl / AfterUrl は別 URL を返さなければならない。これが満たされないと
    // WebView2 は Source 等価で navigation をスキップする。
    [Fact]
    public async Task PrepareMarkdownComparisonPreviewAsync_Switching_Version_Produces_Distinct_Urls()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-switch.git");
        var wtRoot = Path.Combine(_tempRoot, "wt-switch");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty)));
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var sut = BuildSut(
            runner,
            bareCloneDir: bare,
            cloneUrl: "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            previewBasePort: 4500,
            contentServer: contentServer,
            onWorktreeAdd: (path, _) =>
            {
                File.WriteAllText(Path.Combine(path, "CHANGELOG.md"), "# Changelog\n\nEntry");
            });

        var fptLink = await sut.PrepareMarkdownComparisonPreviewAsync(
            123, "headsha", "CHANGELOG.md", version: DocsVersion.Fpt, cancellationToken: ct);
        var ghecLink = await sut.PrepareMarkdownComparisonPreviewAsync(
            123, "headsha", "CHANGELOG.md", version: DocsVersion.Ghec, cancellationToken: ct);

        Assert.NotNull(fptLink);
        Assert.NotNull(ghecLink);
        Assert.NotEqual(fptLink!.BeforeUrl, ghecLink!.BeforeUrl);
        Assert.NotEqual(fptLink.AfterUrl, ghecLink.AfterUrl);
    }

    [Fact]
    public async Task PreparePreviewAsync_With_Same_Sha_Skips_Server_Restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare); // pre-create so EnsureBareCloneAsync is a no-op
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var handle = Substitute.For<IProcessHandle>();
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>()).Returns(handle);
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git",
            worktreeRoot: Path.Combine(_tempRoot, "wt"));

        await sut.PreparePreviewAsync(1, "shaA", "content/a.md", cancellationToken: ct);
        await sut.PreparePreviewAsync(1, "shaA", "content/b.md", cancellationToken: ct);

        // Start should only happen once for the same sha; worktree add only once too.
        runner.Received(1).Start("npm", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>());
        await runner.Received(1).RunAsync(
            "git",
            Arg.Is<string>(s => s.StartsWith("worktree add", StringComparison.Ordinal)),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreparePreviewAsync_With_Different_Sha_Restarts_Server()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var handle = Substitute.For<IProcessHandle>();
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>()).Returns(handle);
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git",
            worktreeRoot: Path.Combine(_tempRoot, "wt"));

        await sut.PreparePreviewAsync(1, "shaA", "content/a.md", cancellationToken: ct);
        await sut.PreparePreviewAsync(1, "shaB", "content/a.md", cancellationToken: ct);

        runner.Received(2).Start("npm", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>());
    }

    [Fact]
    public async Task PreparePreviewAsync_Maps_Non_Content_Path_To_Root()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>())
            .Returns(Substitute.For<IProcessHandle>());
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git",
            worktreeRoot: Path.Combine(_tempRoot, "wt"));

        var link = await sut.PreparePreviewAsync(1, "sha", "data/release-notes/3.10.md", cancellationToken: ct);

        Assert.NotNull(link);
        Assert.Equal("/", link!.Url.AbsolutePath);
    }

    [Fact]
    public async Task PrewarmAsync_Runs_Bare_Clone_Eagerly()
    {
        // Background prewarm during app startup: doing `git clone --bare`
        // (1-2 minutes for github/docs) ahead of time means the user's first
        // preview click skips the slowest non-npm step in the pipeline.
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-prewarm.git");
        var runner = Substitute.For<IProcessRunner>();
        var calls = new List<string>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git");

        await sut.PrewarmAsync(ct);

        Assert.Contains(calls, c => c.StartsWith("git clone --bare", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrewarmAsync_When_Disabled_Is_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var sut = BuildSut(runner, bareCloneDir: string.Empty, cloneUrl: string.Empty);

        await sut.PrewarmAsync(ct);

        await runner.DidNotReceiveWithAnyArgs()
            .RunAsync(default!, default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PredictivePrewarmAsync_Warms_BareClone_Fetch_And_ParentSha()
    {
        // §Step 19.10 (perf): 先読み (PR を選択した瞬間に fire-and-forget で走る)
        // は bare clone / git fetch / 親 SHA 解決までに留める。Markdown preview 本体は
        // git object から必要ファイルだけを読むため、選択だけで巨大 worktree を作らない。
        // 内部の SemaphoreSlim<(prNumber, sha)> がユーザクリック側とのレースを防ぐ。
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-predictive.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-predictive");
        var runner = Substitute.For<IProcessRunner>();
        var calls = new List<string>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        runner.RunAsync("git", "cat-file -e deadbeefcafe^{commit}", bare, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(128, string.Empty, "missing"));
            });
        runner.RunAsync("git", "rev-parse deadbeefcafe^", bare, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot: wtRoot);

        await sut.PredictivePrewarmAsync(prNumber: 4242, sha: "deadbeefcafe", cancellationToken: ct);

        Assert.Contains(calls, c => c.StartsWith("git clone --bare", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.StartsWith("git fetch origin +refs/pull/4242/head:refs/pull/4242/head", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.StartsWith("git rev-parse deadbeefcafe^", StringComparison.Ordinal));
        var worktreeAddCount = calls.Count(c => c.StartsWith("git worktree add", StringComparison.Ordinal));
        Assert.Equal(0, worktreeAddCount);
    }

    [Fact]
    public async Task PrepareMarkdownComparisonPreviewAsync_Reuses_Prewarmed_Session_Without_Refetching()
    {
        // §Step 19.10 (perf): ユーザ視点の中心シナリオ。PR 選択直後に PredictivePrewarmAsync が
        // worktree まで warm up しておけば、最初のファイルクリックでは git fetch も
        // git worktree add も走らず、ファイル単位の Markdown レンダリングだけで済む。
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-reuse.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-reuse");
        var runner = Substitute.For<IProcessRunner>();
        var calls = new List<string>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        runner.RunAsync("git", "cat-file -e headsha^{commit}", bare, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(128, string.Empty, "missing"));
            });
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty));
            });
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var sut = BuildSut(
            runner,
            bare,
            "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            contentServer: contentServer,
            onWorktreeAdd: (path, _) =>
            {
                File.WriteAllText(Path.Combine(path, "CHANGELOG.md"), "# Changelog\n\nentry");
            });

        await sut.PredictivePrewarmAsync(prNumber: 123, sha: "headsha", cancellationToken: ct);
        var prewarmFetchCount = calls.Count(c => c.StartsWith("git fetch origin +refs/pull/", StringComparison.Ordinal));
        var prewarmWorktreeAddCount = calls.Count(c => c.StartsWith("git worktree add", StringComparison.Ordinal));
        Assert.Equal(1, prewarmFetchCount);
        Assert.Equal(0, prewarmWorktreeAddCount);

        var link = await sut.PrepareMarkdownComparisonPreviewAsync(
            123, "headsha", "CHANGELOG.md", cancellationToken: ct);

        Assert.NotNull(link);
        // ユーザクリック後は fetch も worktree add も増えていないこと。
        Assert.Equal(prewarmFetchCount, calls.Count(c => c.StartsWith("git fetch origin +refs/pull/", StringComparison.Ordinal)));
        Assert.Equal(prewarmWorktreeAddCount, calls.Count(c => c.StartsWith("git worktree add", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PrepareMarkdownComparisonPreviewAsync_Switching_Files_Reuses_Cached_Session()
    {
        // §Step 19.10 (perf): 1/7 → 2/7 のファイル切替で「全体をコンパイルしている?」と
        // ユーザが感じるほど遅かった元凶。同一 (prNumber, sha) の 2 回目以降の
        // PrepareMarkdownComparisonPreviewAsync では git fetch / worktree add が
        // 一切走らないことを保証する。
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-fileswitch.git");
        var wtRoot = Path.Combine(_tempRoot, "worktrees-fileswitch");
        var runner = Substitute.For<IProcessRunner>();
        var calls = new List<string>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        runner.RunAsync("git", "cat-file -e headsha^{commit}", bare, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(128, string.Empty, "missing"));
            });
        runner.RunAsync("git", "rev-parse headsha^", bare, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add($"{call.ArgAt<string>(0)} {call.ArgAt<string>(1)}");
                return Task.FromResult(new ProcessRunResult(0, "parentsha\n", string.Empty));
            });
        var contentServer = Substitute.For<ILocalPreviewContentServer>();
        contentServer.StartAsync(
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var sut = BuildSut(
            runner,
            bare,
            "https://example.invalid/docs.git",
            worktreeRoot: wtRoot,
            contentServer: contentServer,
            onWorktreeAdd: (path, _) =>
            {
                // 同じ worktree に複数の Markdown を置いて、ファイル切替時に
                // worktree を作り直していないことを示せるようにする。
                File.WriteAllText(Path.Combine(path, "FILE1.md"), "# File 1\n\nentry");
                File.WriteAllText(Path.Combine(path, "FILE2.md"), "# File 2\n\nentry");
            });

        var firstLink = await sut.PrepareMarkdownComparisonPreviewAsync(
            123, "headsha", "FILE1.md", cancellationToken: ct);
        Assert.NotNull(firstLink);
        var fetchAfterFirst = calls.Count(c => c.StartsWith("git fetch origin +refs/pull/", StringComparison.Ordinal));
        var worktreeAddAfterFirst = calls.Count(c => c.StartsWith("git worktree add", StringComparison.Ordinal));
        Assert.Equal(1, fetchAfterFirst);
        Assert.Equal(0, worktreeAddAfterFirst);

        var secondLink = await sut.PrepareMarkdownComparisonPreviewAsync(
            123, "headsha", "FILE2.md", cancellationToken: ct);

        Assert.NotNull(secondLink);
        Assert.NotEqual(firstLink!.BeforeUrl, secondLink!.BeforeUrl);
        Assert.NotEqual(firstLink.AfterUrl, secondLink.AfterUrl);
        Assert.Contains("file=FILE1.md", firstLink.BeforeUrl.Query, StringComparison.Ordinal);
        Assert.Contains("file=FILE2.md", secondLink.BeforeUrl.Query, StringComparison.Ordinal);
        // 1/7 → 2/7 切替で git fetch / git worktree add が再走していないこと。
        Assert.Equal(fetchAfterFirst, calls.Count(c => c.StartsWith("git fetch origin +refs/pull/", StringComparison.Ordinal)));
        Assert.Equal(worktreeAddAfterFirst, calls.Count(c => c.StartsWith("git worktree add", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PredictivePrewarmAsync_When_Disabled_Is_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var sut = BuildSut(runner, bareCloneDir: string.Empty, cloneUrl: string.Empty);

        await sut.PredictivePrewarmAsync(prNumber: 4242, sha: "deadbeefcafe", cancellationToken: ct);

        await runner.DidNotReceiveWithAnyArgs()
            .RunAsync(default!, default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PredictivePrewarmAsync_Swallows_Fetch_Failure()
    {
        // Best-effort contract: if `git fetch` fails (network down, deleted branch,
        // etc.) the user's later click should still surface the error through the
        // regular path. The predictive call itself must not throw.
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare-predictive-fail.git");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<string>(1);
                if (args.StartsWith("clone --bare", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
                }
                if (args.StartsWith("cat-file -e", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessRunResult(128, string.Empty, "missing"));
                }
                if (args.StartsWith("fetch origin", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessRunResult(128, string.Empty, "fatal: couldn't find remote ref"));
                }
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git");

        // Should not throw.
        await sut.PredictivePrewarmAsync(prNumber: 9999, sha: "deadbeefcafe", cancellationToken: ct);
    }

    [Fact]
    public async Task StopAsync_Deactivates_Session_And_Stops_Server()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var handle = Substitute.For<IProcessHandle>();
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>()).Returns(handle);
        var session = new PreviewSession();
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git",
            worktreeRoot: Path.Combine(_tempRoot, "wt"),
            session: session);
        await sut.PreparePreviewAsync(1, "sha", "content/a.md", cancellationToken: ct);
        Assert.True(session.IsActive);

        await sut.StopAsync(ct);

        Assert.False(session.IsActive);
        await handle.Received(1).KillAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreparePreviewAsync_Reports_Progress_For_Each_Step()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>())
            .Returns(Substitute.For<IProcessHandle>());
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git",
            worktreeRoot: Path.Combine(_tempRoot, "wt-progress"));
        var progress = new ListProgress();

        var link = await sut.PreparePreviewAsync(123, "deadbeefcafe", "content/foo/bar.md", progress, ct);

        Assert.NotNull(link);
        // We expect at least one progress message per major step so the UI can show
        // "what is happening right now" while the (long) pipeline runs.
        Assert.Contains(progress.Items, m => m.Contains("リポジトリ", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("PR", StringComparison.Ordinal));
        Assert.Contains(progress.Items, m => m.Contains("worktree", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress.Items, m => m.Contains("サーバ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanupCacheAsync_Stops_Server_And_Prunes_All_Worktrees()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var wtRoot = Path.Combine(_tempRoot, "wt-cleanup");
        Directory.CreateDirectory(wtRoot);
        var leftover = Path.Combine(wtRoot, "abcdef012345");
        Directory.CreateDirectory(leftover);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                $"worktree {leftover}\nHEAD abcdef0123456789\ndetached\n\n",
                string.Empty)));
        var handle = Substitute.For<IProcessHandle>();
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>()).Returns(handle);
        var session = new PreviewSession();
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git",
            worktreeRoot: wtRoot, session: session);

        // Start a preview so the server + session are active first.
        await sut.PreparePreviewAsync(1, "newshashashash", "content/a.md", cancellationToken: ct);
        Assert.True(session.IsActive);

        var removed = await sut.CleanupCacheAsync(ct);

        // Leftover from previous process (restored from porcelain) + the newly
        // checked-out worktree should both be removed.
        Assert.Equal(2, removed);
        await handle.Received(1).KillAsync(Arg.Any<CancellationToken>());
        Assert.False(session.IsActive);
        await runner.Received().RunAsync("git",
            Arg.Is<string>(a => a.StartsWith("worktree unlock", StringComparison.Ordinal)
                && a.Contains(leftover, StringComparison.Ordinal)),
            bare,
            Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync("git",
            Arg.Is<string>(a => a.StartsWith("worktree remove --force", StringComparison.Ordinal)),
            bare,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupCacheAsync_When_Disabled_Returns_Zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var sut = BuildSut(runner, bareCloneDir: string.Empty, cloneUrl: string.Empty);

        var removed = await sut.CleanupCacheAsync(ct);

        Assert.Equal(0, removed);
        await runner.DidNotReceiveWithAnyArgs()
            .RunAsync(default!, default!, default!, Arg.Any<CancellationToken>());
    }

    private sealed class ListProgress : IProgress<string>
    {
        public List<string> Items { get; } = new();

        public void Report(string value) => Items.Add(value);
    }
    private static PreviewCoordinator BuildSut(
        IProcessRunner runner,
        string bareCloneDir,
        string cloneUrl,
        string? worktreeRoot = null,
        int previewBasePort = 4500,
        PreviewSession? session = null,
        ILocalPreviewContentServer? contentServer = null,
        Action<string, string>? onWorktreeAdd = null,
        IPortReadyProbe? probe = null)
    {
        var options = Options.Create(new DocsRepositoryOptions
        {
            BareCloneDir = bareCloneDir,
            CloneUrl = cloneUrl,
            WorktreeRoot = worktreeRoot ?? string.Empty,
            PreviewCommand = "npm",
            PreviewArguments = "run dev -- --port {port}",
            PreviewBasePort = previewBasePort,
        });
        // Tests mostly exercise the warm path where dependencies already exist.
        // The fake IProcessRunner does not materialize the worktree, so intercept
        // `git worktree add` and git-object reads on disk.
        var objectSourceRoot = Path.Combine(worktreeRoot ?? _fallbackObjectSourceRoot, ".git-object-stubs");
        var materializedObjects = new HashSet<string>(StringComparer.Ordinal);
        void MaterializeObjectSource(string sha)
        {
            if (!materializedObjects.Add(sha))
            {
                return;
            }

            var root = Path.Combine(objectSourceRoot, sha);
            Directory.CreateDirectory(root);
            onWorktreeAdd?.Invoke(root, sha);
        }

        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("show ", StringComparison.Ordinal)),
                bareCloneDir,
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<string>(1);
                var spec = args["show ".Length..];
                var separator = spec.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    return Task.FromResult(new ProcessRunResult(128, string.Empty, "invalid object spec"));
                }

                var sha = spec[..separator];
                var repoPath = spec[(separator + 1)..].TrimStart('/');
                MaterializeObjectSource(sha);
                var file = Path.Combine(objectSourceRoot, sha, repoPath.Replace('/', Path.DirectorySeparatorChar));
                return Task.FromResult(File.Exists(file)
                    ? new ProcessRunResult(0, File.ReadAllText(file), string.Empty)
                    : new ProcessRunResult(128, string.Empty, "path not found"));
            });

        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("ls-tree -r --name-only ", StringComparison.Ordinal)),
                bareCloneDir,
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<string>(1);
                var prefix = "ls-tree -r --name-only ";
                var remainder = args[prefix.Length..];
                var split = remainder.Split(" -- ", 2, StringSplitOptions.None);
                if (split.Length != 2)
                {
                    return Task.FromResult(new ProcessRunResult(128, string.Empty, "invalid ls-tree arguments"));
                }

                var sha = split[0];
                var repoDirectory = split[1].Trim('/');
                MaterializeObjectSource(sha);
                var root = Path.Combine(objectSourceRoot, sha);
                var directory = Path.Combine(root, repoDirectory.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(directory))
                {
                    return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
                }

                var output = string.Join(
                    '\n',
                    Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                        .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')));
                return Task.FromResult(new ProcessRunResult(0, output, string.Empty));
            });

        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("cat-file -e ", StringComparison.Ordinal)
                    && a.Contains(':', StringComparison.Ordinal)),
                bareCloneDir,
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<string>(1);
                if (!TryReadSingleQuotedArgument(args["cat-file -e ".Length..], out var objectSpec))
                {
                    return Task.FromResult(new ProcessRunResult(128, string.Empty, "invalid object spec"));
                }

                var separator = objectSpec.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    return Task.FromResult(new ProcessRunResult(128, string.Empty, "invalid object spec"));
                }

                var sha = objectSpec[..separator];
                var repoPath = objectSpec[(separator + 1)..].TrimStart('/');
                MaterializeObjectSource(sha);
                var file = Path.Combine(objectSourceRoot, sha, repoPath.Replace('/', Path.DirectorySeparatorChar));
                return Task.FromResult(File.Exists(file)
                    ? new ProcessRunResult(0, string.Empty, string.Empty)
                    : new ProcessRunResult(128, string.Empty, "path not found"));
            });

        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("archive --format=tar --output ", StringComparison.Ordinal)),
                bareCloneDir,
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<string>(1);
                if (!TryParseArchiveArguments(args, out var outputPath, out var sha, out var repoPaths))
                {
                    return Task.FromResult(new ProcessRunResult(128, string.Empty, "invalid archive arguments"));
                }

                MaterializeObjectSource(sha);
                var subsetRoot = Path.Combine(Path.GetTempPath(), "rsr-preview-archive-" + Guid.NewGuid().ToString("N"));
                try
                {
                    foreach (var repoPath in repoPaths)
                    {
                        var source = Path.Combine(objectSourceRoot, sha, repoPath.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(source))
                        {
                            continue;
                        }

                        var destination = Path.Combine(subsetRoot, repoPath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(source, destination, overwrite: true);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    TarFile.CreateFromDirectory(subsetRoot, outputPath, includeBaseDirectory: false);
                    return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
                }
                finally
                {
                    if (Directory.Exists(subsetRoot))
                    {
                        Directory.Delete(subsetRoot, recursive: true);
                    }
                }
            });

        runner.WhenForAnyArgs(r => r.RunAsync(default!, default!, default!, default))
            .Do(call =>
            {
                if (!string.Equals(call.ArgAt<string>(0), "git", StringComparison.Ordinal))
                {
                    return;
                }
                var args = call.ArgAt<string>(1);
                if (!args.StartsWith("worktree add", StringComparison.Ordinal))
                {
                    return;
                }
                var parts = args.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var target = parts[2];
                    Directory.CreateDirectory(target);
                    Directory.CreateDirectory(Path.Combine(target, "node_modules"));
                    if (parts.Length >= 4)
                    {
                        onWorktreeAdd?.Invoke(target, parts[3]);
                    }
                }
            });
        var worktree = new DocsWorktreeManager(runner, options, NullLogger<DocsWorktreeManager>.Instance);
        if (probe is null)
        {
            var defaultProbe = Substitute.For<IPortReadyProbe>();
            defaultProbe.WaitForListenAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(),
                Arg.Any<Func<bool>?>(), Arg.Any<CancellationToken>()).Returns(true);
            probe = defaultProbe;
        }
        var server = new PreviewServerHost(runner, probe, options, NullLogger<PreviewServerHost>.Instance);
        var serverFactory = new PreviewServerHostFactory(
            runner,
            probe,
            options,
            NullLogger<PreviewServerHost>.Instance);
        session ??= new PreviewSession();
        return new PreviewCoordinator(
            worktree,
            server,
            serverFactory,
            contentServer ?? Substitute.For<ILocalPreviewContentServer>(),
            session,
            new FixedPreviewPortAllocator(previewBasePort),
            options,
            NullLogger<PreviewCoordinator>.Instance);
    }

    private static readonly string _fallbackObjectSourceRoot = Path.Combine(
        Path.GetTempPath(),
        "rsr-preview-coord-object-stubs");

    private sealed class FixedPreviewPortAllocator(int basePort) : IPreviewPortAllocator
    {
        public int AllocateSingle(int preferredPort, IReadOnlyCollection<int> reusablePorts) => preferredPort;

        public PreviewPortPair AllocateComparison(int preferredAfterPort, IReadOnlyCollection<int> reusablePorts)
            => new(basePort, basePort + 1);
    }

    private static bool TryParseArchiveArguments(
        string args,
        out string outputPath,
        out string sha,
        out IReadOnlyList<string> repoPaths)
    {
        outputPath = string.Empty;
        sha = string.Empty;
        repoPaths = Array.Empty<string>();
        const string prefix = "archive --format=tar --output ";
        if (!args.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = args[prefix.Length..];
        if (!TryReadSingleQuotedArgument(remainder, out outputPath, out var consumed))
        {
            return false;
        }

        remainder = remainder[consumed..].TrimStart();
        if (!TryReadSingleQuotedArgument(remainder, out sha, out consumed))
        {
            return false;
        }

        remainder = remainder[consumed..].TrimStart();
        if (!remainder.StartsWith("-- ", StringComparison.Ordinal))
        {
            return false;
        }

        remainder = remainder["-- ".Length..];
        var paths = new List<string>();
        while (remainder.Length > 0)
        {
            if (!TryReadSingleQuotedArgument(remainder, out var path, out consumed))
            {
                return false;
            }
            paths.Add(path);
            remainder = remainder[consumed..].TrimStart();
        }

        repoPaths = paths;
        return true;
    }

    private static bool TryReadSingleQuotedArgument(string input, out string value)
        => TryReadSingleQuotedArgument(input, out value, out _);

    private static bool TryReadSingleQuotedArgument(string input, out string value, out int consumed)
    {
        value = string.Empty;
        consumed = 0;
        if (input.Length == 0 || input[0] != '"')
        {
            return false;
        }

        var parsed = new StringBuilder();
        var backslashCount = 0;
        for (var index = 1; index < input.Length; index++)
        {
            var ch = input[index];
            if (ch == '\\')
            {
                backslashCount++;
                continue;
            }

            if (ch == '"')
            {
                parsed.Append('\\', backslashCount / 2);
                if (backslashCount % 2 == 0)
                {
                    value = parsed.ToString();
                    consumed = index + 1;
                    return true;
                }

                parsed.Append('"');
                backslashCount = 0;
                continue;
            }

            parsed.Append('\\', backslashCount);
            backslashCount = 0;
            parsed.Append(ch);
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }
}
