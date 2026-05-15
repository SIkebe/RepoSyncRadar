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
        Assert.Equal(4, calls.Count);
        Assert.StartsWith("RUN git clone --bare", calls[0], StringComparison.Ordinal);
        Assert.StartsWith("RUN git fetch origin +refs/pull/123/head:refs/pull/123/head", calls[1], StringComparison.Ordinal);
        Assert.StartsWith("RUN git worktree add", calls[2], StringComparison.Ordinal);
        Assert.StartsWith("START npm run dev -- --port 4500", calls[3], StringComparison.Ordinal);
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
            Arg.Is<string>(a => a.StartsWith("worktree remove --force", StringComparison.Ordinal)
                && a.Contains(leftover, StringComparison.Ordinal)),
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
        PreviewSession? session = null)
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
        // `git worktree add` and stub node_modules on disk.
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
                }
            });
        var worktree = new DocsWorktreeManager(runner, options, NullLogger<DocsWorktreeManager>.Instance);
        var probe = Substitute.For<IPortReadyProbe>();
        probe.WaitForListenAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(),
            Arg.Any<Func<bool>?>(), Arg.Any<CancellationToken>()).Returns(true);
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
            session,
            new FixedPreviewPortAllocator(previewBasePort),
            options,
            NullLogger<PreviewCoordinator>.Instance);
    }

    private sealed class FixedPreviewPortAllocator(int basePort) : IPreviewPortAllocator
    {
        public int AllocateSingle(int preferredPort, IReadOnlyCollection<int> reusablePorts) => preferredPort;

        public PreviewPortPair AllocateComparison(int preferredAfterPort, IReadOnlyCollection<int> reusablePorts)
            => new(basePort, basePort + 1);
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
