using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotInProcessRuntimeTests
{
    [Fact]
    public async Task PingAsync_Connects_To_Bundled_InProcess_Runtime()
    {
        var options = CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions(),
            NullLogger<CopilotSessionFactory>.Instance,
            "test-token-for-local-ping",
            "0.1.30");
        await using var client = new CopilotClient(options);

        var response = await client.PingAsync("in-process", TestContext.Current.CancellationToken);
        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("pong: in-process", response.Message);
        Assert.Equal("1.0.92-4", status.Version);
    }
}
