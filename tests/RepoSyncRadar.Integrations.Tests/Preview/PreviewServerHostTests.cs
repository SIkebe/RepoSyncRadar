using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Integrations.Tests.Preview;

/// <summary>
/// Unit tests for <see cref="PreviewServerHost"/> (IMPLEMENTATION_PLAN.md §Step 19.3).
/// </summary>
public sealed class PreviewServerHostTests : IDisposable
{
    private readonly string _wt;

    public PreviewServerHostTests()
    {
        _wt = Path.Combine(Path.GetTempPath(),
            "rsr-preview-server-host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wt);
        // Most tests exercise the warm path where dependencies already exist.
        Directory.CreateDirectory(Path.Combine(_wt, "node_modules"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_wt))
            {
                Directory.Delete(_wt, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Start_Spawns_Process_With_Port()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var handle = Substitute.For<IProcessHandle>();
        runner.Start("npm", "run dev -- --port 4500", _wt,
            Arg.Any<IReadOnlyDictionary<string, string?>?>()).Returns(handle);
        var probe = Substitute.For<IPortReadyProbe>();
        probe.WaitForListenAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(),
            Arg.Any<Func<bool>?>(), Arg.Any<CancellationToken>()).Returns(true);
        var sut = BuildSut(runner, probe, command: "npm", arguments: "run dev -- --port {port}");

        var returned = await sut.StartAsync(_wt, 4500, ct);

        Assert.Same(handle, returned);
        runner.Received(1).Start("npm", "run dev -- --port 4500", _wt,
            Arg.Any<IReadOnlyDictionary<string, string?>?>());
        Assert.Equal(4500, sut.CurrentPort);
    }

    [Fact]
    public async Task DisposeAsync_Kills_Process()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var handle = Substitute.For<IProcessHandle>();
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>()).Returns(handle);
        var probe = Substitute.For<IPortReadyProbe>();
        probe.WaitForListenAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(),
            Arg.Any<Func<bool>?>(), Arg.Any<CancellationToken>()).Returns(true);
        var sut = BuildSut(runner, probe, command: "npm", arguments: "run dev -- --port {port}");
        await sut.StartAsync(_wt, 4501, ct);

        await sut.DisposeAsync();

        await handle.Received(1).KillAsync(Arg.Any<CancellationToken>());
        await handle.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_Reports_Dev_Server_Progress_On_Warm_Path()
    {
        // When node_modules already exists, install is skipped but the "dev サーバを起動中"
        // message must still fire so the UI can flip from the worktree-prep phase
        // to a more specific compile phase.
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var handle = Substitute.For<IProcessHandle>();
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>()).Returns(handle);
        var probe = Substitute.For<IPortReadyProbe>();
        probe.WaitForListenAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(),
            Arg.Any<Func<bool>?>(), Arg.Any<CancellationToken>()).Returns(true);
        var sut = BuildSut(runner, probe, command: "npm", arguments: "run dev -- --port {port}");
        var progress = new CapturingProgress();

        await sut.StartAsync(_wt, 4500, progress, ct);

        Assert.Contains(progress.Messages, m => m.Contains("Next.js", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Messages, m => m.Contains("node_modules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_Reports_Install_Phase_When_Node_Modules_Missing()
    {
        // When node_modules is missing the install phase can take minutes. The
        // UI must be able to distinguish it from the much faster Next.js compile
        // phase, otherwise the user has no way to tell whether the install is
        // hung or normally progressing.
        var ct = TestContext.Current.CancellationToken;
        var cold = Path.Combine(Path.GetTempPath(), "rsr-psh-cold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cold);
        try
        {
            var runner = Substitute.For<IProcessRunner>();
            var handle = Substitute.For<IProcessHandle>();
            runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>()).Returns(handle);
            var probe = Substitute.For<IPortReadyProbe>();
            probe.WaitForListenAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(),
                Arg.Any<Func<bool>?>(), Arg.Any<CancellationToken>()).Returns(true);

            // The default IShareManager defers to the install callback when no
            // package-lock.json exists. The callback runs `npm install`, so we
            // need to satisfy that too with a handle that exits cleanly.
            var installHandle = Substitute.For<IProcessHandle>();
            installHandle.WaitForExitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(0));
            runner.Start("npm", "install", cold,
                Arg.Any<IReadOnlyDictionary<string, string?>?>()).Returns(installHandle);

            var sut = BuildSut(runner, probe, command: "npm", arguments: "run dev -- --port {port}");
            var progress = new CapturingProgress();

            await sut.StartAsync(cold, 4500, progress, ct);

            Assert.Contains(progress.Messages, m => m.Contains("node_modules", StringComparison.Ordinal));
            Assert.Contains(progress.Messages, m => m.Contains("Next.js", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(cold, recursive: true); } catch (IOException) { }
        }
    }

    private static PreviewServerHost BuildSut(
        IProcessRunner runner,
        IPortReadyProbe probe,
        string command,
        string arguments)
    {
        var options = Options.Create(new DocsRepositoryOptions
        {
            PreviewCommand = command,
            PreviewArguments = arguments,
            PreviewInstallArguments = "install",
            PreviewEnvironment = new Dictionary<string, string>(),
        });
        return new PreviewServerHost(runner, probe, options, NullLogger<PreviewServerHost>.Instance);
    }

    private sealed class CapturingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }
}
