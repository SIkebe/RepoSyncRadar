using System;
using System.Collections.Generic;
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
public sealed class PreviewServerHostTests
{
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

        var handle = await host.StartAsync("C:\\worktree", 4500, TestContext.Current.CancellationToken);

        Assert.NotNull(handle);
        var call = Assert.Single(runner.StartCalls);
        Assert.Equal("npm", call.FileName);
        Assert.Equal("run dev -- --port 4500", call.Arguments);
        Assert.Equal("C:\\worktree", call.WorkingDirectory);
        Assert.NotNull(call.Environment);
        Assert.Equal("4500", call.Environment!["PORT"]);
        Assert.Equal("development", call.Environment["NODE_ENV"]);

        var probed = Assert.Single(probe.Calls);
        Assert.Equal(4500, probed.Port);
        Assert.Equal(TimeSpan.FromSeconds(5), probed.Timeout);
    }

    [Fact]
    public async Task StartAsync_Throws_When_Probe_Times_Out()
    {
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewReadyTimeoutSeconds = 1;
        });
        probe.NextResult = false;
        runner.NextHandle = new FakeHandle { HasExited = false };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync("C:\\worktree", 4500, TestContext.Current.CancellationToken));

        Assert.Contains("4500", ex.Message);
        Assert.Contains("待ち受け状態", ex.Message);
        Assert.True(runner.NextHandle.Killed);
    }

    [Fact]
    public async Task StartAsync_Throws_When_Child_Exits_Early()
    {
        // Simulates the failure mode where npm script crashes before listening
        // (e.g. missing dependency). The probe reports !ready but liveness check
        // already returned false; the exception message must reflect that.
        var host = CreateHost(out var runner, out var probe, options =>
        {
            options.PreviewCommand = "npm";
            options.PreviewReadyTimeoutSeconds = 1;
        });
        probe.NextResult = false;
        runner.NextHandle = new FakeHandle { HasExited = true };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync("C:\\worktree", 4500, TestContext.Current.CancellationToken));

        Assert.Contains("起動直後に終了", ex.Message);
    }

    private static PreviewServerHost CreateHost(
        out FakeProcessRunner runner,
        out FakeReadyProbe probe,
        Action<DocsRepositoryOptions>? configure = null)
    {
        var options = new DocsRepositoryOptions();
        configure?.Invoke(options);
        runner = new FakeProcessRunner();
        probe = new FakeReadyProbe();
        return new PreviewServerHost(
            runner,
            probe,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<PreviewServerHost>.Instance);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<StartCall> StartCalls { get; } = [];
        public FakeHandle NextHandle { get; set; } = new();

        public Task<ProcessRunResult> RunAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct)
            => throw new NotSupportedException();

        public IProcessHandle Start(
            string fileName,
            string arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string?>? environment)
        {
            StartCalls.Add(new StartCall(fileName, arguments, workingDirectory,
                environment is null ? null : environment.ToDictionary(kv => kv.Key, kv => kv.Value)));
            return NextHandle;
        }
    }

    private sealed record StartCall(
        string FileName,
        string Arguments,
        string WorkingDirectory,
        Dictionary<string, string?>? Environment);

    private sealed class FakeReadyProbe : IPortReadyProbe
    {
        public List<ProbeCall> Calls { get; } = [];
        public bool NextResult { get; set; } = true;

        public Task<bool> WaitForListenAsync(
            int port, TimeSpan timeout, Func<bool>? processStillAlive, CancellationToken ct)
        {
            Calls.Add(new ProbeCall(port, timeout));
            return Task.FromResult(NextResult);
        }
    }

    private sealed record ProbeCall(int Port, TimeSpan Timeout);

    private sealed class FakeHandle : IProcessHandle
    {
        public int ProcessId => 1234;
        public bool HasExited { get; set; }
        public bool Killed { get; private set; }
        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(0);
        public Task KillAsync(CancellationToken ct = default)
        {
            Killed = true;
            HasExited = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => default;
    }
}
