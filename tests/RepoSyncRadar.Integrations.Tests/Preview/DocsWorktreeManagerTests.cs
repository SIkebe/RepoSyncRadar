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
            "fetch origin +refs/pull/123/head:refs/pull/123/head",
            bare,
            Arg.Any<CancellationToken>());
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
        var path = await sut.CheckoutAsync("abcdef0123", ct);

        Assert.Null(path);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(
            default!,
            default!,
            default!,
            Arg.Any<CancellationToken>());
    }

    private static DocsWorktreeManager BuildSut(
        IProcessRunner runner,
        string bareCloneDir,
        string cloneUrl,
        string? worktreeRoot = null,
        int maxWorktrees = 5)
    {
        var options = Options.Create(new DocsRepositoryOptions
        {
            BareCloneDir = bareCloneDir,
            CloneUrl = cloneUrl,
            WorktreeRoot = worktreeRoot ?? string.Empty,
            MaxWorktrees = maxWorktrees,
        });
        return new DocsWorktreeManager(runner, options, NullLogger<DocsWorktreeManager>.Instance);
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
    }
}
