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

    [Fact]
    public async Task StartAsync_Waits_For_Complete_Request_Headers_Before_Responding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new LocalPreviewContentServer(NullLogger<LocalPreviewContentServer>.Instance);
        var port = GetFreeLoopbackPort();
        using var client = new TcpClient();

        await server.StartAsync(
            port,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/markdown/before"] = "<html><body>page</body></html>",
            },
            ct);
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(
            "GET /markdown/before HTTP/1.1\r\nHost: 127.0.0.1\r\n"u8.ToArray(),
            ct);

        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        Assert.False(stream.DataAvailable);

        await stream.WriteAsync("\r\n"u8.ToArray(), ct);
        using var reader = new StreamReader(stream);
        var response = await reader.ReadToEndAsync(ct);
        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
        Assert.Contains("<html><body>page</body></html>", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_Serves_Asset_Root_Files_With_Content_Type()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new LocalPreviewContentServer(NullLogger<LocalPreviewContentServer>.Instance);
        var port = GetFreeLoopbackPort();
        var root = Path.Combine(Path.GetTempPath(), "rsr-preview-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets", "images"));
        var imagePath = Path.Combine(root, "assets", "images", "sample.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47], ct);
        using var http = new HttpClient();

        try
        {
            await server.StartAsync(
                port,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/markdown/after"] = "<html><body>page</body></html>",
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/markdown-assets/after"] = root,
                },
                ct);

            using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/markdown-assets/after/assets/images/sample.png"), ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal([0x89, 0x50, 0x4e, 0x47], bytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_Serves_MediaTypeMap_Content_Types()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new LocalPreviewContentServer(NullLogger<LocalPreviewContentServer>.Instance);
        var port = GetFreeLoopbackPort();
        var root = Path.Combine(Path.GetTempPath(), "rsr-preview-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        var cgmPath = Path.Combine(root, "assets", "diagram.cgm");
        var svgPath = Path.Combine(root, "assets", "diagram.svg");
        await File.WriteAllBytesAsync(cgmPath, [0x43, 0x47, 0x4d], ct);
        await File.WriteAllTextAsync(svgPath, "<svg xmlns=\"http://www.w3.org/2000/svg\" />", ct);
        using var http = new HttpClient();

        try
        {
            await server.StartAsync(
                port,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/markdown/after"] = "<html><body>page</body></html>",
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/markdown-assets/after"] = root,
                },
                ct);

            using var cgmResponse = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/markdown-assets/after/assets/diagram.cgm"), ct);
            using var svgResponse = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/markdown-assets/after/assets/diagram.svg"), ct);

            Assert.Equal(HttpStatusCode.OK, cgmResponse.StatusCode);
            Assert.Equal("image/cgm", cgmResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal(HttpStatusCode.OK, svgResponse.StatusCode);
            Assert.Equal("image/svg+xml", svgResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", svgResponse.Content.Headers.ContentType?.CharSet);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_Rejects_Asset_Path_Traversal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new LocalPreviewContentServer(NullLogger<LocalPreviewContentServer>.Instance);
        var port = GetFreeLoopbackPort();
        var root = Path.Combine(Path.GetTempPath(), "rsr-preview-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var http = new HttpClient();

        try
        {
            await server.StartAsync(
                port,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/markdown/after"] = "<html><body>page</body></html>",
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/markdown-assets/after"] = root,
                },
                ct);

            using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/markdown-assets/after/%2e%2e/outside.png"), ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        return endpoint.Port;
    }
}