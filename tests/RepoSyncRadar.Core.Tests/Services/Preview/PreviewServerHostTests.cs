using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="PreviewServerHost"/> covering the two regressions that
/// shipped to a user: (1) the <c>{port}</c> placeholder was only substituted in
/// <c>PreviewArguments</c> — never in <c>PreviewEnvironment</c>, so the
/// <c>github/docs</c> server ignored the requested port and listened on its
/// built-in 4000; (2) the host returned immediately after spawning the child,
/// before <c>nodemon</c>'s cold start finished, so WebView2 navigated to a port
/// that was not yet accepting connections and showed "接続できません".
/// </summary>
public sealed class PreviewServerHostTests : IDisposable
{
    private readonly string _sandboxRoot;

    public PreviewServerHostTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(),
            "rsr-preview-host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandboxRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandboxRoot))
            {
                Directory.Delete(_sandboxRoot, recursive: true);
            }
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    /// <summary>Creates a worktree path under the test sandbox with <c>node_modules</c> stubbed in.</summary>
    private string CreateWorktree(bool withNodeModules = true)
    {
        var wt = Path.Combine(_sandboxRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wt);
        if (withNodeModules)
        {
            Directory.CreateDirectory(Path.Combine(wt, "node_modules"));
        }
        return wt;
    }

    [Fact]
    public void ReplacePort_Substitutes_All_Placeholders()
    {
        var result = PreviewServerHost.ReplacePort("run dev --port {port} --inspect={port}", 4501);

        Assert.Equal("run dev --port 4501 --inspect=4501", result);
    }

    [Fact]
    public void BuildEnvironment_Substitutes_Port_In_Values()
    {
        // The exact case that was broken: PORT={port} must come out as PORT=4500
        // so github/docs' nodemon actually honors the requested port.
        var template = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PORT"] = "{port}",
            ["NODE_ENV"] = "development",
        };

        var result = PreviewServerHost.BuildEnvironment(template, port: 4500);

        Assert.NotNull(result);
        Assert.Equal("4500", result["PORT"]);
        Assert.Equal("development", result["NODE_ENV"]);
    }

    [Fact]
    public void BuildEnvironment_Returns_Null_When_Template_Is_Null_Or_Empty()
    {
        Assert.Null(PreviewServerHost.BuildEnvironment(null, port: 4500));
        Assert.Null(PreviewServerHost.BuildEnvironment(new Dictionary<string, string>(), port: 4500));
    }

    [Fact]
    public void WithDefaultRequestTimeout_Adds_Default_For_Npm_When_Missing()
    {
        var template = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PORT"] = "{port}",
        };

        var result = PreviewServerHost.WithDefaultRequestTimeout(template, "npm");

        Assert.NotNull(result);
        Assert.Equal("600000", result["REQUEST_TIMEOUT"]);
        Assert.Equal("{port}", result["PORT"]);
    }

    [Fact]
    public void WithDefaultRequestTimeout_Preserves_Configured_Value()
    {
        var template = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REQUEST_TIMEOUT"] = "120000",
        };

        var result = PreviewServerHost.WithDefaultRequestTimeout(template, "npm.cmd");

        Assert.NotNull(result);
        Assert.Equal("120000", result["REQUEST_TIMEOUT"]);
    }

    [Fact]
    public void WithDefaultRequestTimeout_Skips_Non_Npm_Commands()
    {
        var result = PreviewServerHost.WithDefaultRequestTimeout(null, "hugo");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("npm", true)]
    [InlineData("NPM", true)]
    [InlineData("npm.cmd", true)]
    [InlineData("pnpm", true)]
    [InlineData("yarn", true)]
    [InlineData("C:\\Program Files\\nodejs\\npm.cmd", true)]
    [InlineData("hugo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNpmCommand_Recognizes_Node_Package_Managers(string? command, bool expected)
    {
        Assert.Equal(expected, PreviewServerHost.IsNpmCommand(command));
    }

    [Fact]
    public void SnapshotTail_Returns_Last_N_Lines_Indented()
    {
        var lines = new[] { "one", "two", "three", "four", "five" };

        var tail = PreviewServerHost.SnapshotTail(lines, take: 3);

        Assert.Equal("  three\r\n  four\r\n  five".Replace("\r\n", Environment.NewLine, StringComparison.Ordinal), tail);
    }

    [Fact]
    public void SnapshotTail_Returns_Empty_For_No_Lines()
    {
        Assert.Equal(string.Empty, PreviewServerHost.SnapshotTail((IReadOnlyList<string>?)null, 5));
        Assert.Equal(string.Empty, PreviewServerHost.SnapshotTail(Array.Empty<string>(), 5));
    }

    [Fact]
    public async Task StartAsync_Returns_Null_When_Disabled()
    {
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "";
        });

        var handle = await host.StartAsync("C:\\worktree", 4500, TestContext.Current.CancellationToken);

        Assert.Null(handle);
        Assert.Empty(runner.StartCalls);
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public async Task StartAsync_Forwards_Port_To_Arguments_And_Environment()
    {
        var worktree = CreateWorktree();
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewArguments = "run dev -- --port {port}";
            options.PreviewEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PORT"] = "{port}",
                ["NODE_ENV"] = "development",
            };
            options.PreviewReadyTimeoutSeconds = 5;
        });
        probe.NextResult = true;

        var handle = await host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken);

        Assert.NotNull(handle);
        var call = Assert.Single(runner.StartCalls);
        Assert.Equal("npm", call.FileName);
        Assert.Equal("run dev -- --port 4500", call.Arguments);
        Assert.Equal(worktree, call.WorkingDirectory);
        Assert.NotNull(call.Environment);
        Assert.Equal("4500", call.Environment!["PORT"]);
        Assert.Equal("development", call.Environment["NODE_ENV"]);
        Assert.Equal("600000", call.Environment["REQUEST_TIMEOUT"]);

        var probed = Assert.Single(probe.Calls);
        Assert.Equal(4500, probed.Port);
        Assert.Equal(TimeSpan.FromSeconds(5), probed.Timeout);
    }

    [Fact]
    public async Task StartAsync_Cleans_Stale_Next_Server_Before_Npm_Start()
    {
        var worktree = CreateWorktree();
        var cleaner = new FakeProcessCleaner(1);
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewReadyTimeoutSeconds = 5;
        }, cleaner);
        probe.NextResult = true;

        await host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken);

        var call = Assert.Single(cleaner.Calls);
        Assert.Equal(worktree, call.WorktreePath);
        Assert.Null(call.StartupFailureOutput);
        Assert.Single(runner.StartCalls);
    }

    [Fact]
    public async Task StartAsync_Retries_Once_When_Next_Reports_Duplicate_Server()
    {
        var worktree = CreateWorktree();
        var cleaner = new FakeProcessCleaner(0, 1);
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewReadyTimeoutSeconds = 5;
        }, cleaner);
        var failedHandle = new FakeHandle
        {
            HasExited = true,
            RecentStderrLines = new[]
            {
                "× Another next dev server is already running. - Local: http://localhost:3000 - PID: 32776 - Dir: "
                    + worktree + " - Log: .next\\dev\\logs\\next-development.log Run taskkill /PID 32776 /F to stop it.",
            },
        };
        var successHandle = new FakeHandle { HasExited = false };
        runner.EnqueueHandles(failedHandle, successHandle);
        probe.OnInvoked = () => probe.NextResult = probe.Calls.Count > 1;

        var returned = await host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken);

        Assert.Same(successHandle, returned);
        Assert.True(failedHandle.Killed);
        Assert.Equal(2, runner.StartCalls.Count);
        Assert.Equal(2, probe.Calls.Count);
        Assert.Equal(2, cleaner.Calls.Count);
        Assert.Null(cleaner.Calls[0].StartupFailureOutput);
        Assert.Contains("Another next dev server", cleaner.Calls[1].StartupFailureOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_Throws_With_Recent_Stderr_When_Probe_Times_Out()
    {
        var worktree = CreateWorktree();
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewReadyTimeoutSeconds = 1;
        });
        probe.NextResult = false;
        runner.NextHandle = new FakeHandle
        {
            HasExited = false,
            RecentStderrLines = new[] { "EADDRINUSE: address already in use :::4500" },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken));

        Assert.Contains("4500", ex.Message);
        Assert.Contains("待ち受け状態", ex.Message);
        Assert.Contains("EADDRINUSE", ex.Message);
        Assert.True(runner.NextHandle.Killed);
    }

    [Fact]
    public async Task StartAsync_Includes_Stdout_When_Stderr_Empty_And_Process_Exited()
    {
        var worktree = CreateWorktree();
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewReadyTimeoutSeconds = 1;
        });
        probe.NextResult = false;
        runner.NextHandle = new FakeHandle
        {
            HasExited = true,
            RecentStdoutLines = new[] { "npm warn config production", "Lifecycle script ended" },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken));

        Assert.Contains("起動直後に終了", ex.Message);
        Assert.Contains("Lifecycle script ended", ex.Message);
    }

    [Fact]
    public async Task StartAsync_Runs_Install_When_Node_Modules_Missing()
    {
        // If cleanup removed node_modules (or this SHA's worktree is new), the
        // preview should repair itself instead of asking the user to run npm.
        var worktree = CreateWorktree(withNodeModules: false);
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewInstallArguments = "install";
            options.PreviewReadyTimeoutSeconds = 5;
        });
        probe.NextResult = true;
        var installHandle = new FakeHandle { WaitExitCode = 0 };
        var serverHandle = new FakeHandle { HasExited = false };
        runner.EnqueueHandles(installHandle, serverHandle);

        var returned = await host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken);

        Assert.Same(serverHandle, returned);
        Assert.Empty(runner.RunCalls);
        Assert.Equal(2, runner.StartCalls.Count);
        var install = runner.StartCalls[0];
        Assert.Equal("npm", install.FileName);
        Assert.Equal("install", install.Arguments);
        Assert.Equal(worktree, install.WorkingDirectory);
        Assert.True(installHandle.Disposed);
        Assert.Single(probe.Calls);
    }

    [Fact]
    public async Task StartAsync_Throws_When_Install_Fails()
    {
        var worktree = CreateWorktree(withNodeModules: false);
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewInstallArguments = "install";
        });
        runner.NextHandle = new FakeHandle
        {
            WaitExitCode = 1,
            RecentStdoutLines = new[] { "install stdout" },
            RecentStderrLines = new[] { "npm ERR! install failed" },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken));

        Assert.Contains("npm install", ex.Message);
        Assert.Contains("npm ERR!", ex.Message);
        Assert.Empty(runner.RunCalls);
        Assert.Single(runner.StartCalls);
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public async Task StartAsync_Skips_Node_Modules_Check_For_Non_Npm_Command()
    {
        // Non-Node preview commands (hugo, jekyll, ...) must keep working
        // without a node_modules directory.
        var worktree = CreateWorktree(withNodeModules: false);
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "hugo";
            options.PreviewArguments = "serve";
            options.PreviewReadyTimeoutSeconds = 5;
        });
        probe.NextResult = true;

        var handle = await host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken);

        Assert.NotNull(handle);
        Assert.Empty(runner.RunCalls);
        Assert.Single(runner.StartCalls);
    }

    [Fact]
    public void RecentStdoutLines_Is_Empty_When_No_Process_Running()
    {
        // Before any StartAsync the UI's polling loop must see an empty array,
        // not a null reference — the Razor component renders this directly.
        var host = CreateHost(out _, out _);

        Assert.NotNull(host.RecentStdoutLines);
        Assert.Empty(host.RecentStdoutLines);
        Assert.NotNull(host.RecentStderrLines);
        Assert.Empty(host.RecentStderrLines);
        Assert.False(host.IsProcessRunning);
        Assert.Null(host.CurrentProcessId);
    }

    [Fact]
    public async Task RecentLines_Reflect_Current_Process_While_Probe_Is_Waiting()
    {
        // The whole point of P1: while StartAsync is blocked inside
        // WaitForListenAsync (npm starting up), the UI must be able to read
        // RecentStdoutLines / RecentStderrLines so it can show a live tail.
        var worktree = CreateWorktree();
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewReadyTimeoutSeconds = 5;
        });
        var fakeHandle = new FakeHandle
        {
            HasExited = false,
            RecentStdoutLines = new[] { "  ▲ Next.js 15.5.4", "  - Local: http://localhost:4500" },
            RecentStderrLines = new[] { "  ⚠ Compiled with warnings" },
        };
        runner.NextHandle = fakeHandle;

        // FakeReadyProbe runs synchronously, but we intercept right before it
        // returns: read the live snapshot via the host while _current is set.
        probe.OnInvoked = () =>
        {
            Assert.True(host.IsProcessRunning);
            Assert.Equal(fakeHandle.RecentStdoutLines, host.RecentStdoutLines);
            Assert.Equal(fakeHandle.RecentStderrLines, host.RecentStderrLines);
            // PID surfaced so the UI can show it for Task Manager / netstat lookup.
            Assert.Equal(fakeHandle.ProcessId, host.CurrentProcessId);
        };
        probe.NextResult = true;

        await host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecentLines_Return_Empty_After_StopAsync()
    {
        // After teardown the UI must not keep showing stale logs from the
        // previous run.
        var worktree = CreateWorktree();
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewReadyTimeoutSeconds = 5;
        });
        probe.NextResult = true;
        runner.NextHandle = new FakeHandle
        {
            HasExited = false,
            RecentStdoutLines = new[] { "old line" },
        };

        await host.StartAsync(worktree, 4500, TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(host.RecentStdoutLines);
        Assert.Empty(host.RecentStderrLines);
        Assert.False(host.IsProcessRunning);
    }

    [Fact]
    public async Task StartAsync_Kills_Child_When_Probe_Is_Cancelled()
    {
        // P1-F: cancel during the WaitForListenAsync stage must tear down the
        // child process — otherwise an orphan npm keeps the port held and the
        // next preview attempt fails with "EADDRINUSE".
        var worktree = CreateWorktree();
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewReadyTimeoutSeconds = 60;
        });
        var fakeHandle = new FakeHandle { HasExited = false };
        runner.NextHandle = fakeHandle;
        using var cts = new CancellationTokenSource();
        probe.OnInvoked = () => cts.Cancel();
        probe.ThrowOnCancellation = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.StartAsync(worktree, 4500, cts.Token));

        Assert.True(fakeHandle.Killed);
        Assert.False(host.IsProcessRunning);
        Assert.Empty(host.RecentStdoutLines);
    }

    private static PreviewServerHost CreateHost(
        out FakeProcessRunner runner,
        out FakeReadyProbe probe,
        Action<DocsRepositoryOptions>? configure = null,
        IPreviewServerProcessCleaner? processCleaner = null)
    {
        var options = new DocsRepositoryOptions();
        configure?.Invoke(options);
        runner = new FakeProcessRunner();
        probe = new FakeReadyProbe();
        return new PreviewServerHost(
            runner,
            probe,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<PreviewServerHost>.Instance,
            processCleaner ?? NoopPreviewServerProcessCleaner.Instance);
    }

    private sealed class FakeProcessCleaner(params int[] results) : IPreviewServerProcessCleaner
    {
        private readonly Queue<int> _results = new(results);

        public List<CleanCall> Calls { get; } = [];

        public Task<int> StopStaleServersAsync(
            string worktreePath,
            string? startupFailureOutput = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new CleanCall(worktreePath, startupFailureOutput));
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : 0);
        }
    }

    private sealed record CleanCall(string WorktreePath, string? StartupFailureOutput);

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<RunCall> RunCalls { get; } = [];
        public List<StartCall> StartCalls { get; } = [];
        public ProcessRunResult NextRunResult { get; set; } = new(0, string.Empty, string.Empty);
        public FakeHandle NextHandle { get; set; } = new();
        private readonly Queue<FakeHandle> _queuedHandles = new();

        public void EnqueueHandles(params FakeHandle[] handles)
        {
            foreach (var handle in handles)
            {
                _queuedHandles.Enqueue(handle);
            }
        }

        public Task<ProcessRunResult> RunAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct)
        {
            RunCalls.Add(new RunCall(fileName, arguments, workingDirectory));
            return Task.FromResult(NextRunResult);
        }

        public IProcessHandle Start(
            string fileName,
            string arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string?>? environment)
        {
            StartCalls.Add(new StartCall(fileName, arguments, workingDirectory,
                environment is null ? null : environment.ToDictionary(kv => kv.Key, kv => kv.Value)));
            return _queuedHandles.Count > 0 ? _queuedHandles.Dequeue() : NextHandle;
        }
    }

    private sealed record RunCall(string FileName, string Arguments, string WorkingDirectory);

    private sealed record StartCall(
        string FileName,
        string Arguments,
        string WorkingDirectory,
        Dictionary<string, string?>? Environment);

    private sealed class FakeReadyProbe : IPortReadyProbe
    {
        public List<ProbeCall> Calls { get; } = [];
        public bool NextResult { get; set; } = true;
        public Action? OnInvoked { get; set; }
        public bool ThrowOnCancellation { get; set; }

        public Task<bool> WaitForListenAsync(
            int port, TimeSpan timeout, Func<bool>? processStillAlive, CancellationToken ct)
        {
            Calls.Add(new ProbeCall(port, timeout));
            OnInvoked?.Invoke();
            if (ThrowOnCancellation && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            return Task.FromResult(NextResult);
        }
    }

    private sealed record ProbeCall(int Port, TimeSpan Timeout);

    private sealed class FakeHandle : IProcessHandle
    {
        public int ProcessId => 1234;
        public bool HasExited { get; set; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public int WaitExitCode { get; set; }
        public IReadOnlyList<string> RecentStdoutLines { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> RecentStderrLines { get; set; } = Array.Empty<string>();
        public Task<int> WaitForExitAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            HasExited = true;
            return Task.FromResult(WaitExitCode);
        }
        public Task KillAsync(CancellationToken ct = default)
        {
            Killed = true;
            HasExited = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }
}
