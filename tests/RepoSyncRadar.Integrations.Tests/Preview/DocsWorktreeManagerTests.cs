using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Integrations.Tests.Preview;

/// <summary>
/// Unit tests for the docs worktree LRU manager (IMPLEMENTATION_PLAN.md §Step 19.3).
/// The real <c>git</c> CLI is never invoked — <see cref="IProcessRunner"/> is stubbed
/// via NSubstitute so the tests stay fast and deterministic across machines.
/// </summary>
public sealed class DocsWorktreeManagerTests : IDisposable
{
    private readonly string _tempRoot;

    public DocsWorktreeManagerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "rsr-worktree-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task EnsureBareCloneAsync_Skips_When_Exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        var sut = BuildSut(runner, bareCloneDir: bare, cloneUrl: "https://example.invalid/docs.git");

        await sut.EnsureBareCloneAsync(ct);

        await runner.DidNotReceive().RunAsync(
            Arg.Any<string>(),
            Arg.Is<string>(a => a.Contains("clone", StringComparison.Ordinal)),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBareCloneAsync_Quotes_Clone_Url_And_Target_Path()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "user with space", "github docs.git");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bareCloneDir: bare, cloneUrl: "https://example.invalid/docs.git");

        await sut.EnsureBareCloneAsync(ct);

        await runner.Received(1).RunAsync(
            "git",
            Arg.Is<string>(args => args.StartsWith("-c maintenance.auto=false clone --bare ", StringComparison.Ordinal)
                && args.Contains("\"https://example.invalid/docs.git\"", StringComparison.Ordinal)
                && args.Contains('"' + bare + '"', StringComparison.Ordinal)),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchPrAsync_Builds_Correct_Refspec()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bareCloneDir: bare, cloneUrl: "https://example.invalid/docs.git");

        await sut.FetchPrAsync(123, ct);

        await runner.Received(1).RunAsync(
            "git",
            "-c maintenance.auto=false fetch origin +refs/pull/123/head:refs/pull/123/head",
            bare,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureCommitAvailableAsync_Skips_Fetch_When_Commit_Exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "cat-file -e abcdef^{commit}", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bareCloneDir: bare, cloneUrl: "https://example.invalid/docs.git");

        await sut.EnsureCommitAvailableAsync(123, "abcdef", cancellationToken: ct);

        await runner.Received(1).RunAsync(
            "git",
            "cat-file -e abcdef^{commit}",
            bare,
            Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync(
            "git",
            Arg.Is<string>(a => a.StartsWith("-c maintenance.auto=false fetch origin", StringComparison.Ordinal)),
            bare,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureCommitAvailableAsync_Fetches_When_Commit_Is_Missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "cat-file -e abcdef^{commit}", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(128, string.Empty, "missing")));
        runner.RunAsync("git", "-c maintenance.auto=false fetch origin +refs/pull/123/head:refs/pull/123/head", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bareCloneDir: bare, cloneUrl: "https://example.invalid/docs.git");

        await sut.EnsureCommitAvailableAsync(123, "abcdef", cancellationToken: ct);

        await runner.Received(1).RunAsync(
            "git",
            "-c maintenance.auto=false fetch origin +refs/pull/123/head:refs/pull/123/head",
            bare,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveFirstParentAsync_Uses_Git_RevParse()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "rev-parse abcdef^", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "parent123\n", string.Empty)));
        var sut = BuildSut(runner, bareCloneDir: bare, cloneUrl: "https://example.invalid/docs.git");

        var parent = await sut.ResolveFirstParentAsync("abcdef", ct);

        Assert.Equal("parent123", parent);
    }

    [Fact]
    public async Task CheckoutAsync_Reuses_Existing_Worktree()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var first = await sut.CheckoutAsync("abcdef0123456789", ct);
        var second = await sut.CheckoutAsync("abcdef0123456789", ct);

        Assert.Equal(first, second);
        await runner.Received(1).RunAsync(
            "git",
            Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
            bare,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lru_Evicts_Oldest()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot, maxWorktrees: 3);

        var sha1 = "aaaaaaaaaaaaaa01";
        var sha2 = "bbbbbbbbbbbbbb02";
        var sha3 = "cccccccccccccc03";
        var sha4 = "dddddddddddddd04";
        var path1 = await sut.CheckoutAsync(sha1, ct);
        await sut.CheckoutAsync(sha2, ct);
        await sut.CheckoutAsync(sha3, ct);
        await sut.CheckoutAsync(sha4, ct);

        await runner.Received(1).RunAsync(
            "git",
            Arg.Is<string>(a => a.StartsWith("worktree remove --force", StringComparison.Ordinal)
                && a.Contains(path1!, StringComparison.Ordinal)),
            bare,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_When_Path_Empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var sut = BuildSut(runner, bareCloneDir: string.Empty, cloneUrl: string.Empty);

        Assert.False(sut.IsEnabled);
        await sut.EnsureBareCloneAsync(ct);
        await sut.FetchPrAsync(1, ct);
        await sut.EnsureCommitAvailableAsync(1, "abcdef0123", cancellationToken: ct);
        var path = await sut.CheckoutAsync("abcdef0123", ct);

        Assert.Null(path);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(
            default!,
            default!,
            default!,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckoutAsync_Reuses_Worktree_From_Previous_Process()
    {
        // Regression: the in-memory LRU was empty on every app start, so worktrees on
        // disk from a previous run were never garbage-collected and just piled up. The
        // manager must scan `git worktree list --porcelain` once on first use to
        // rehydrate its state from disk.
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var existingSha = "abcdef0123456789";
        var existingPath = Path.Combine(worktreeRoot, "abcdef012345");
        Directory.CreateDirectory(existingPath);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync(
                "git",
                "worktree list --porcelain",
                bare,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                $"worktree {bare}\nbare\n\nworktree {existingPath}\nHEAD {existingSha}\ndetached\n\n",
                string.Empty)));
        runner.RunAsync("git", "reset --hard", existingPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var path = await sut.CheckoutAsync(existingSha, ct);

        Assert.Equal(existingPath, path);
        // No new worktree add should be issued — the existing on-disk worktree is reused.
        await runner.DidNotReceive().RunAsync(
            "git",
            Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            "git",
            "reset --hard",
            existingPath,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckoutAsync_Resets_InMemory_Reused_Worktree_Before_Returning()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "abcdef0123456789";
        var expectedPath = Path.Combine(worktreeRoot, "abcdef012345");
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var first = await sut.CheckoutAsync(sha, ct);
        var second = await sut.CheckoutAsync(sha, ct);

        Assert.Equal(expectedPath, first);
        Assert.Equal(first, second);
        await runner.Received(1).RunAsync(
            "git",
            "reset --hard",
            expectedPath,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckoutAsync_Removes_Stale_IndexLock_And_Retries_Reset()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "abababababab0001";
        var expectedPath = Path.Combine(worktreeRoot, "abababababab");
        var gitDir = Path.Combine(bare, "worktrees", "abababababab");
        var lockPath = Path.Combine(gitDir, "index.lock");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
                bare,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Directory.CreateDirectory(expectedPath);
                Directory.CreateDirectory(gitDir);
                File.WriteAllText(Path.Combine(expectedPath, ".git"), $"gitdir: {gitDir}\n");
                File.WriteAllText(lockPath, string.Empty);
                File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - TimeSpan.FromMinutes(10));
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        runner.RunAsync("git", "reset --hard", expectedPath, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new ProcessRunResult(
                    128,
                    string.Empty,
                    $"fatal: Unable to create '{lockPath}': File exists.")),
                Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        await sut.CheckoutAsync(sha, ct);
        var path = await sut.CheckoutAsync(sha, ct);

        Assert.Equal(expectedPath, path);
        Assert.False(File.Exists(lockPath));
        await runner.Received(2).RunAsync(
            "git",
            "reset --hard",
            expectedPath,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckoutAsync_Recreates_Locked_Initializing_Worktree_Even_When_Head_Matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "cdcdcdcdcdcd0002";
        var existingPath = Path.Combine(worktreeRoot, "cdcdcdcdcdcd");
        var gitDir = Path.Combine(bare, "worktrees", "cdcdcdcdcdcd");
        Directory.CreateDirectory(existingPath);
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(existingPath, ".git"), $"gitdir: {gitDir}\n");
        File.WriteAllText(Path.Combine(gitDir, "locked"), "initializing\n");
        var partialFile = Path.Combine(existingPath, "partial.txt");
        File.WriteAllText(partialFile, "checkout was interrupted");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                $"worktree {existingPath}\nHEAD {sha}\ndetached\nlocked initializing\n\n",
                string.Empty)));
        runner.RunAsync("git", "worktree unlock " + existingPath, bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "worktree remove --force --force " + existingPath, bare, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Directory.Delete(existingPath, recursive: true);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
                bare,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.False(File.Exists(partialFile));
                Directory.CreateDirectory(existingPath);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var path = await sut.CheckoutAsync(sha, ct);

        Assert.Equal(existingPath, path);
        await runner.Received(1).RunAsync("git", "worktree unlock " + existingPath, bare, Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync("git", "worktree remove --force --force " + existingPath, bare, Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            "git",
            Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
            bare,
            Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync("git", "reset --hard", existingPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckoutAsync_Cleans_Partial_Worktree_When_Add_Is_Canceled()
    {
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "edededededed0003";
        var expectedPath = Path.Combine(worktreeRoot, "edededededed");
        var partialFile = Path.Combine(expectedPath, "partial.txt");
        using var cts = new CancellationTokenSource();

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
                bare,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Directory.CreateDirectory(expectedPath);
                File.WriteAllText(partialFile, "checkout interrupted");
                cts.Cancel();
                return Task.FromCanceled<ProcessRunResult>(cts.Token);
            });
        runner.RunAsync("git", "worktree unlock " + expectedPath, bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "worktree remove --force --force " + expectedPath, bare, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Directory.Delete(expectedPath, recursive: true);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        await Assert.ThrowsAsync<TaskCanceledException>(() => sut.CheckoutAsync(sha, cts.Token));

        Assert.False(Directory.Exists(expectedPath));
        await runner.Received(1).RunAsync("git", "worktree unlock " + expectedPath, bare, Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync("git", "worktree remove --force --force " + expectedPath, bare, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckoutAsync_Cleans_Partial_Worktree_When_Add_Fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "efefefefefef0004";
        var expectedPath = Path.Combine(worktreeRoot, "efefefefefef");
        var partialFile = Path.Combine(expectedPath, "partial.txt");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
                bare,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Directory.CreateDirectory(expectedPath);
                File.WriteAllText(partialFile, "checkout failed");
                return Task.FromResult(new ProcessRunResult(128, string.Empty, "checkout failed"));
            });
        runner.RunAsync("git", "worktree unlock " + expectedPath, bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "worktree remove --force --force " + expectedPath, bare, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Directory.Delete(expectedPath, recursive: true);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CheckoutAsync(sha, ct));

        Assert.Contains("worktree add failed", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(expectedPath));
        await runner.Received(1).RunAsync("git", "worktree unlock " + expectedPath, bare, Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync("git", "worktree remove --force --force " + expectedPath, bare, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRepairExistingWorktreeAsync_Returns_True_After_Resetting_Healthy_Worktree()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        var path = Path.Combine(worktreeRoot, "aaaaaaaaaaaa");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(path);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "reset --hard", path, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var repaired = await sut.TryRepairExistingWorktreeAsync(path, ct);

        Assert.True(repaired);
        await runner.Received(1).RunAsync("git", "reset --hard", path, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRepairExistingWorktreeAsync_Removes_Locked_Initializing_Worktree_And_Returns_False()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        var path = Path.Combine(worktreeRoot, "bbbbbbbbbbbb");
        var gitDir = Path.Combine(bare, "worktrees", "bbbbbbbbbbbb");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(path, ".git"), $"gitdir: {gitDir}\n");
        File.WriteAllText(Path.Combine(gitDir, "locked"), "initializing\n");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree unlock " + path, bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "worktree remove --force --force " + path, bare, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Directory.Delete(path, recursive: true);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var repaired = await sut.TryRepairExistingWorktreeAsync(path, ct);

        Assert.False(repaired);
        Assert.False(Directory.Exists(path));
        await runner.Received(1).RunAsync("git", "worktree remove --force --force " + path, bare, Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync("git", "reset --hard", path, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckoutAsync_Evicts_Worktree_From_Previous_Process_When_Over_Limit()
    {
        // After rehydration the on-disk worktrees count toward MaxWorktrees, so adding
        // a fresh checkout that pushes over the limit must `git worktree remove` the
        // oldest leftover. This is what stops C:\github\.cache\docs-worktrees from
        // growing unbounded across restarts.
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var oldPath = Path.Combine(worktreeRoot, "aaaaaaaaaaaa");
        Directory.CreateDirectory(oldPath);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync(
                "git",
                "worktree list --porcelain",
                bare,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                $"worktree {oldPath}\nHEAD aaaaaaaaaaaaaa01\ndetached\n\n",
                string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot, maxWorktrees: 1);

        await sut.CheckoutAsync("bbbbbbbbbbbbbb02", ct);

        await runner.Received(1).RunAsync(
            "git",
            Arg.Is<string>(a => a.StartsWith("worktree remove --force", StringComparison.Ordinal)
                && a.Contains(oldPath, StringComparison.Ordinal)),
            bare,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckoutAsync_Patches_Next_Custom_Server_To_Use_Webpack()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "eeeeeeeeeeeeee05";
        var expectedPath = Path.Combine(worktreeRoot, "eeeeeeeeeeee");
        var nextFile = Path.Combine(expectedPath, "src", "frame", "middleware", "next.ts");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
                bare,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(nextFile)!);
                File.WriteAllText(nextFile, "export const nextApp = next({ dev: isDevelopment })\n");
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var path = await sut.CheckoutAsync(sha, ct);

        Assert.Equal(expectedPath, path);
        Assert.Contains(
            "export const nextApp = next({ dev: isDevelopment, webpack: true })",
            await File.ReadAllTextAsync(nextFile, ct));
    }

    [Fact]
    public async Task CheckoutAsync_Reuses_Untracked_Existing_Directory_When_Head_Matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "ffffffffffff0006";
        var existingPath = Path.Combine(worktreeRoot, "ffffffffffff");
        Directory.CreateDirectory(existingPath);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse HEAD", existingPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, sha + "\n", string.Empty)));
        runner.RunAsync("git", "reset --hard", existingPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var path = await sut.CheckoutAsync(sha, ct);

        Assert.Equal(existingPath, path);
        await runner.DidNotReceive().RunAsync(
            "git",
            Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
            bare,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckoutAsync_Deletes_Stale_Existing_Directory_Before_Adding_Worktree()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "9999999999990007";
        var stalePath = Path.Combine(worktreeRoot, "999999999999");
        Directory.CreateDirectory(stalePath);
        var staleFile = Path.Combine(stalePath, "partial.txt");
        File.WriteAllText(staleFile, "left from interrupted checkout");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse HEAD", stalePath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(128, string.Empty, "not a git repository")));
        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
                bare,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.False(File.Exists(staleFile));
                Directory.CreateDirectory(stalePath);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var path = await sut.CheckoutAsync(sha, ct);

        Assert.Equal(stalePath, path);
    }

    [Fact]
    public async Task CheckoutAsync_Clears_Delete_Blocking_Attributes_When_Deleting_Stale_Directory()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "3434343434340009";
        var stalePath = Path.Combine(worktreeRoot, "343434343434");
        Directory.CreateDirectory(stalePath);
        var staleFile = Path.Combine(stalePath, "readonly.txt");
        File.WriteAllText(staleFile, "left from interrupted npm install");
        File.SetAttributes(staleFile, FileAttributes.ReadOnly | FileAttributes.Hidden);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse HEAD", stalePath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(128, string.Empty, "not a git repository")));
        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
                bare,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.False(File.Exists(staleFile));
                Directory.CreateDirectory(stalePath);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var path = await sut.CheckoutAsync(sha, ct);

        Assert.Equal(stalePath, path);
    }

    [Fact]
    public async Task CheckoutAsync_Stops_Stale_Server_Before_Deleting_Stale_Existing_Directory()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var sha = "1212121212120008";
        var stalePath = Path.Combine(worktreeRoot, "121212121212");
        Directory.CreateDirectory(stalePath);
        var staleFile = Path.Combine(stalePath, "partial.txt");
        File.WriteAllText(staleFile, "left from interrupted checkout");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "rev-parse HEAD", stalePath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(128, string.Empty, "not a git repository")));
        runner.RunAsync(
                "git",
                Arg.Is<string>(a => a.StartsWith("worktree add", StringComparison.Ordinal)),
                bare,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.False(File.Exists(staleFile));
                Directory.CreateDirectory(stalePath);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });
        var cleaner = Substitute.For<IPreviewServerProcessCleaner>();
        cleaner.StopStaleServersAsync(stalePath, null, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.True(Directory.Exists(stalePath));
                return Task.FromResult(1);
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot, processCleaner: cleaner);

        var path = await sut.CheckoutAsync(sha, ct);

        Assert.Equal(stalePath, path);
        await cleaner.Received(1).StopStaleServersAsync(stalePath, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneAllAsync_Removes_All_Tracked_Worktrees()
    {
        // Surface from the UI / docs so users can deliberately wipe the cache.
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var p1 = Path.Combine(worktreeRoot, "aaaaaaaaaaaa");
        var p2 = Path.Combine(worktreeRoot, "bbbbbbbbbbbb");
        Directory.CreateDirectory(p1);
        Directory.CreateDirectory(p2);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                $"worktree {p1}\nHEAD aaaaaaaaaaaaaa01\ndetached\n\n" +
                $"worktree {p2}\nHEAD bbbbbbbbbbbbbb02\ndetached\n\n",
                string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var removed = await sut.PruneAllAsync(ct);

        Assert.Equal(2, removed);
        await runner.Received().RunAsync("git",
            Arg.Is<string>(a => a.StartsWith("worktree unlock", StringComparison.Ordinal)
                && a.Contains(p1, StringComparison.Ordinal)),
            bare, Arg.Any<CancellationToken>());
        await runner.Received().RunAsync("git",
            Arg.Is<string>(a => a.StartsWith("worktree unlock", StringComparison.Ordinal)
                && a.Contains(p2, StringComparison.Ordinal)),
            bare, Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync("git",
            Arg.Is<string>(a => a.StartsWith("worktree remove --force", StringComparison.Ordinal)),
            bare, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneAllAsync_Stops_Stale_Servers_Before_Removing_Worktrees()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var path = Path.Combine(worktreeRoot, "aaaaaaaaaaaa");
        Directory.CreateDirectory(path);
        var cleanerCalled = false;

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                $"worktree {path}\nHEAD aaaaaaaaaaaaaa01\ndetached\n\n",
                string.Empty)));
        var cleaner = Substitute.For<IPreviewServerProcessCleaner>();
        cleaner.StopStaleServersAsync(path, null, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cleanerCalled = true;
                return Task.FromResult(1);
            });
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot, processCleaner: cleaner);

        var removed = await sut.PruneAllAsync(ct);

        Assert.Equal(1, removed);
    Assert.True(cleanerCalled);
        await cleaner.Received(1).StopStaleServersAsync(path, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadFileTextAsync_Reads_File_From_Bare_Clone_Object()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "show abc123:content/foo.md", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "# Foo", string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", Path.Combine(_tempRoot, "wt"));

        var content = await sut.ReadFileTextAsync("abc123", "content/foo.md", ct);

        Assert.Equal("# Foo", content);
    }

    [Fact]
    public async Task ListFilesAsync_Lists_Matching_Files_From_Bare_Clone_Object()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "ls-tree -r --name-only abc123 -- content", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                "content/a.md\ncontent/b.yml\ncontent/nested/c.md\n",
                string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", Path.Combine(_tempRoot, "wt"));

        var files = await sut.ListFilesAsync("abc123", "content", ".md", ct);

        Assert.Equal(["content/a.md", "content/nested/c.md"], files);
    }

    [Fact]
    public async Task FindFilesContainingAsync_Strips_Treeish_Prefix_From_GitGrep_Output()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", "grep -l -F -- \"old-route\" abc123 -- content", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                "abc123:content/a.md\nabc123:content/b.yml\nabc123:content/nested/c.md\n",
                string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", Path.Combine(_tempRoot, "wt"));

        var files = await sut.FindFilesContainingAsync("abc123", "content", "old-route", ".md", ct);

        Assert.Equal(["content/a.md", "content/nested/c.md"], files);
    }

    [Fact]
    public async Task PruneAllAsync_Ignores_Already_Detached_Worktree_Metadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var missingPath = Path.Combine(worktreeRoot, "missing-worktree");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                $"worktree {missingPath}\nHEAD abcdef0123456789\ndetached\n\n",
                string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var removed = await sut.PruneAllAsync(ct);

        Assert.Equal(0, removed);
        await runner.DidNotReceive().RunAsync("git",
            Arg.Is<string>(a => a.StartsWith("worktree unlock", StringComparison.Ordinal)),
            bare, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneAllAsync_Detaches_Untracked_Worktree_Directories()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "wt");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(worktreeRoot);
        var stalePath = Path.Combine(worktreeRoot, "stale-directory");
        Directory.CreateDirectory(stalePath);
        File.WriteAllText(Path.Combine(stalePath, "leftover.txt"), "stale");

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync("git", "worktree list --porcelain", bare, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var removed = await sut.PruneAllAsync(ct);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stalePath));
        var pendingRoot = Path.Combine(worktreeRoot, ".delete-pending");
        Assert.True(Directory.Exists(pendingRoot));
        await WaitForBackgroundDeletesAsync(pendingRoot, ct);
    }

    private static DocsWorktreeManager BuildSut(
        IProcessRunner runner,
        string bareCloneDir,
        string cloneUrl,
        string? worktreeRoot = null,
        int maxWorktrees = 5,
        IPreviewServerProcessCleaner? processCleaner = null)
    {
        var options = Options.Create(new DocsRepositoryOptions
        {
            BareCloneDir = bareCloneDir,
            CloneUrl = cloneUrl,
            WorktreeRoot = worktreeRoot ?? string.Empty,
            MaxWorktrees = maxWorktrees,
        });
        return new DocsWorktreeManager(
            runner,
            options,
            NullLogger<DocsWorktreeManager>.Instance,
            processCleaner ?? NoopPreviewServerProcessCleaner.Instance);
    }

    private static async Task WaitForBackgroundDeletesAsync(string pendingRoot, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(pendingRoot) || !Directory.EnumerateDirectories(pendingRoot).Any())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
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
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
