using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Integrations.Tests.Preview;

/// <summary>
/// Unit tests for <see cref="NodeModulesShareManager"/>. The manager's job is
/// to skip the (extremely slow) 5-15 minute <c>npm install</c> when a previous
/// worktree with the same <c>package-lock.json</c> already populated a shared
/// store. Tests mock the <see cref="IProcessRunner"/> mklink invocation and
/// observe the side effects on disk (sentinel files, store directories) plus
/// the number of times the install fallback runs.
/// </summary>
public sealed class NodeModulesShareManagerTests : IDisposable
{
    private readonly string _tempRoot;

    public NodeModulesShareManagerTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "rsr-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
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
            // Junction targets in the share store can keep the temp directory
            // pinned on Windows even after the test exits; swallow so the
            // suite never fails on cleanup.
        }
    }

    [Fact]
    public async Task EnsureAsync_With_No_PackageLock_Runs_Install_Fallback_Without_Junction()
    {
        var ct = TestContext.Current.CancellationToken;
        var wt = Path.Combine(_tempRoot, "wt-1");
        Directory.CreateDirectory(wt);
        var runner = Substitute.For<IProcessRunner>();
        var sut = BuildSut(runner);
        var installCount = 0;

        await sut.EnsureAsync(
            wt,
            _ => { installCount++; return Task.CompletedTask; },
            ct);

        Assert.Equal(1, installCount);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureAsync_With_Empty_WorktreeRoot_Falls_Back_To_Install()
    {
        var ct = TestContext.Current.CancellationToken;
        var wt = Path.Combine(_tempRoot, "wt-1");
        Directory.CreateDirectory(wt);
        await File.WriteAllTextAsync(Path.Combine(wt, "package-lock.json"), "{\"name\":\"docs\"}", ct);
        var runner = Substitute.For<IProcessRunner>();
        var sut = BuildSut(runner, worktreeRoot: string.Empty);
        var installCount = 0;

        await sut.EnsureAsync(
            wt,
            _ => { installCount++; return Task.CompletedTask; },
            ct);

        Assert.Equal(1, installCount);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureAsync_First_Time_Creates_Junction_Then_Installs_Then_Writes_Complete_Sentinel()
    {
        var ct = TestContext.Current.CancellationToken;
        var wt = Path.Combine(_tempRoot, "wt-1");
        Directory.CreateDirectory(wt);
        await File.WriteAllTextAsync(
            Path.Combine(wt, "package-lock.json"),
            "{\"name\":\"docs\",\"lockfileVersion\":3}",
            ct);

        var runner = Substitute.For<IProcessRunner>();
        var mklinkArgs = new List<string>();
        runner.RunAsync("cmd", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                mklinkArgs.Add(call.ArgAt<string>(1));
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            });

        var sut = BuildSut(runner);
        var installOrder = new List<string>();
        await sut.EnsureAsync(
            wt,
            _ => { installOrder.Add("install"); return Task.CompletedTask; },
            ct);

        Assert.Single(mklinkArgs);
        Assert.Contains("mklink /J", mklinkArgs[0], StringComparison.Ordinal);
        Assert.Contains(Path.Combine(wt, "node_modules"), mklinkArgs[0], StringComparison.Ordinal);
        Assert.Single(installOrder);
        Assert.Equal("install", installOrder[0]);

        var storeRoot = Path.Combine(_tempRoot, ".shared-node-modules");
        Assert.True(Directory.Exists(storeRoot));
        var slot = Directory.GetDirectories(storeRoot).Single();
        Assert.True(File.Exists(Path.Combine(slot, ".complete")));
    }

    [Fact]
    public async Task EnsureAsync_When_Complete_Sentinel_Exists_Only_Creates_Junction_And_Skips_Install()
    {
        var ct = TestContext.Current.CancellationToken;
        var wt1 = Path.Combine(_tempRoot, "wt-1");
        var wt2 = Path.Combine(_tempRoot, "wt-2");
        Directory.CreateDirectory(wt1);
        Directory.CreateDirectory(wt2);
        var lockContent = "{\"name\":\"docs\",\"lockfileVersion\":3,\"packages\":{}}";
        await File.WriteAllTextAsync(Path.Combine(wt1, "package-lock.json"), lockContent, ct);
        await File.WriteAllTextAsync(Path.Combine(wt2, "package-lock.json"), lockContent, ct);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("cmd", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty)));

        var sut = BuildSut(runner);
        var installCount = 0;

        await sut.EnsureAsync(wt1, _ => { installCount++; return Task.CompletedTask; }, ct);
        await sut.EnsureAsync(wt2, _ => { installCount++; return Task.CompletedTask; }, ct);

        Assert.Equal(1, installCount);
        await runner.Received(2).RunAsync(
            "cmd",
            Arg.Is<string>(s => s.Contains("mklink /J", StringComparison.Ordinal)),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureAsync_When_Junction_Fails_Runs_Install_And_Does_Not_Mark_Complete()
    {
        var ct = TestContext.Current.CancellationToken;
        var wt = Path.Combine(_tempRoot, "wt-1");
        Directory.CreateDirectory(wt);
        await File.WriteAllTextAsync(
            Path.Combine(wt, "package-lock.json"),
            "{\"name\":\"docs\",\"lockfileVersion\":3,\"packages\":{}}",
            ct);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("cmd", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessRunResult(1, string.Empty, "Access is denied")));

        var sut = BuildSut(runner);
        var installCount = 0;
        await sut.EnsureAsync(wt, _ => { installCount++; return Task.CompletedTask; }, ct);

        Assert.Equal(1, installCount);
        var storeRoot = Path.Combine(_tempRoot, ".shared-node-modules");
        if (Directory.Exists(storeRoot))
        {
            foreach (var slot in Directory.GetDirectories(storeRoot))
            {
                Assert.False(File.Exists(Path.Combine(slot, ".complete")));
            }
        }
    }

    private NodeModulesShareManager BuildSut(IProcessRunner runner, string? worktreeRoot = null)
    {
        var options = Options.Create(new DocsRepositoryOptions
        {
            WorktreeRoot = worktreeRoot ?? _tempRoot,
        });
        return new NodeModulesShareManager(
            runner,
            options,
            NullLogger<NodeModulesShareManager>.Instance);
    }
}
