using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Integrations.Tests.Preview;

public sealed class LocalPreviewContentServerTests
{
    [Fact]
    public async Task StartAsync_When_Same_Port_Is_Already_Running_Updates_Pages_In_Place()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new LocalPreviewContentServer(NullLogger<LocalPreviewContentServer>.Instance);
        var port = GetFreeLoopbackPort();
        using var http = new HttpClient();

        await server.StartAsync(
            port,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/markdown/before"] = "<html><body>old page</body></html>",
            },
            ct);
        var first = await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/markdown/before?v=fpt&file=one.md"), ct);

        await server.StartAsync(
            port,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/markdown/before"] = "<html><body>new page</body></html>",
            },
            ct);
        var second = await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/markdown/before?v=fpt&file=two.md"), ct);

        Assert.True(server.IsRunning);
        Assert.Equal(port, server.CurrentPort);
        Assert.Contains("old page", first, StringComparison.Ordinal);
        Assert.Contains("new page", second, StringComparison.Ordinal);
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        return endpoint.Port;
    }
}