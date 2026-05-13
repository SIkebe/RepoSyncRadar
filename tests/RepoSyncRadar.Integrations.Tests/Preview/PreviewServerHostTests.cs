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
public sealed class PreviewServerHostTests
{
    [Fact]
    public async Task Start_Spawns_Process_With_Port()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var handle = Substitute.For<IProcessHandle>();
        runner.Start("npm", "run dev -- --port 4500", "C:/wt").Returns(handle);
        var sut = BuildSut(runner, command: "npm", arguments: "run dev -- --port {port}");

        var returned = await sut.StartAsync("C:/wt", 4500, ct);

        Assert.Same(handle, returned);
        runner.Received(1).Start("npm", "run dev -- --port 4500", "C:/wt");
        Assert.Equal(4500, sut.CurrentPort);
    }

    [Fact]
    public async Task DisposeAsync_Kills_Process()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = Substitute.For<IProcessRunner>();
        var handle = Substitute.For<IProcessHandle>();
        runner.Start(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(handle);
        var sut = BuildSut(runner, command: "npm", arguments: "run dev -- --port {port}");
        await sut.StartAsync("C:/wt", 4501, ct);

        await sut.DisposeAsync();

        await handle.Received(1).KillAsync(Arg.Any<CancellationToken>());
        await handle.Received(1).DisposeAsync();
    }

    private static PreviewServerHost BuildSut(IProcessRunner runner, string command, string arguments)
    {
        var options = Options.Create(new DocsRepositoryOptions
        {
            PreviewCommand = command,
            PreviewArguments = arguments,
        });
        return new PreviewServerHost(runner, options, NullLogger<PreviewServerHost>.Instance);
    }
}
