using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Integrations.Tests.Preview;

public sealed class DocsWorktreeManagerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "rsr-worktree-tests-" + Guid.NewGuid().ToString("N"));

    public DocsWorktreeManagerTests()
    {
        Directory.CreateDirectory(_tempRoot);
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
    public async Task ReadFileTextAsync_Reads_From_BareClone_By_Sha()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(
                "git",
                Arg.Is<string>(args => args.Contains("--git-dir ", StringComparison.Ordinal)
                    && args.Contains('"' + bare + '"', StringComparison.Ordinal)
                    && args.EndsWith(" show abcdef:content/index.md", StringComparison.Ordinal)),
                Path.GetDirectoryName(bare)!,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, "# Home", string.Empty)));
        var sut = BuildSut(runner, bareCloneDir: bare, cloneUrl: "https://example.invalid/docs.git");

        var text = await sut.ReadFileTextAsync("abcdef", "content/index.md", ct);

        Assert.Equal("# Home", text);
    }

    [Fact]
    public async Task ResolvePreviousPathAsync_Returns_Renamed_Source_Path()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        Directory.CreateDirectory(bare);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(
                "git",
                Arg.Is<string>(args => args.Contains("--git-dir ", StringComparison.Ordinal)
                    && args.Contains('"' + bare + '"', StringComparison.Ordinal)
                    && args.EndsWith(" diff --name-status --find-renames -z parentsha headsha", StringComparison.Ordinal)),
                Path.GetDirectoryName(bare)!,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                "R098\0content/old.md\0content/new.md\0",
                string.Empty)));
        var sut = BuildSut(runner, bareCloneDir: bare, cloneUrl: "https://example.invalid/docs.git");

        var previousPath = await sut.ResolvePreviousPathAsync(
            "parentsha",
            "headsha",
            "content/new.md",
            ct);

        Assert.Equal("content/old.md", previousPath);
    }

    [Fact]
    public async Task PruneAllAsync_Removes_Restored_And_Untracked_Worktrees()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = Path.Combine(_tempRoot, "bare.git");
        var worktreeRoot = Path.Combine(_tempRoot, "worktrees");
        var tracked = Path.Combine(worktreeRoot, "tracked");
        var untracked = Path.Combine(worktreeRoot, "untracked");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(tracked);
        Directory.CreateDirectory(untracked);
        var bareParent = Path.GetDirectoryName(bare)!;
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("git", Arg.Any<string>(), bareParent, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));
        runner.RunAsync(
                "git",
                Arg.Is<string>(args => args.Contains("--git-dir ", StringComparison.Ordinal)
                    && args.Contains('"' + bare + '"', StringComparison.Ordinal)
                    && args.EndsWith(" worktree list --porcelain", StringComparison.Ordinal)),
                bareParent,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(
                0,
                $"worktree {tracked}\nHEAD abcdef0123456789\ndetached\n\n",
                string.Empty)));
        var sut = BuildSut(runner, bare, "https://example.invalid/docs.git", worktreeRoot);

        var removed = await sut.PruneAllAsync(ct);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(tracked));
        Assert.False(Directory.Exists(untracked));
        await runner.Received().RunAsync(
            "git",
            Arg.Is<string>(args => args.Contains("--git-dir ", StringComparison.Ordinal)
                && args.Contains('"' + bare + '"', StringComparison.Ordinal)
                && args.Contains(" worktree unlock ", StringComparison.Ordinal)),
            bareParent,
            Arg.Any<CancellationToken>());
    }

    private static DocsWorktreeManager BuildSut(
        IProcessRunner runner,
        string bareCloneDir,
        string cloneUrl,
        string? worktreeRoot = null)
    {
        var options = Options.Create(new DocsRepositoryOptions
        {
            BareCloneDir = bareCloneDir,
            CloneUrl = cloneUrl,
            WorktreeRoot = worktreeRoot ?? Path.Combine(Path.GetTempPath(), "rsr-worktrees"),
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
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
