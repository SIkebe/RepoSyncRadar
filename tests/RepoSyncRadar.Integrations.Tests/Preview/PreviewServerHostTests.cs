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
            PreviewEnvironment = new Dictionary<string, string>(),
        });
        return new PreviewServerHost(runner, probe, options, NullLogger<PreviewServerHost>.Instance);
    }
}
