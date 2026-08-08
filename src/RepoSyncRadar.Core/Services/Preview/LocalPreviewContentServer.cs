using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RepoSyncRadar.Core.Services.Preview;

public interface ILocalPreviewContentServer
{
    bool IsRunning { get; }

    int CurrentPort { get; }

    Task StartAsync(
        int port,
        IReadOnlyDictionary<string, string> pages,
        CancellationToken cancellationToken = default);

    Task StartAsync(
        int port,
        IReadOnlyDictionary<string, string> pages,
        IReadOnlyDictionary<string, string> assetRoots,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed partial class LocalPreviewContentServer : ILocalPreviewContentServer, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly ILogger<LocalPreviewContentServer> _logger;
    private Dictionary<string, byte[]> _pages = new(StringComparer.Ordinal);
    private Dictionary<string, string> _assetRoots = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public LocalPreviewContentServer(ILogger<LocalPreviewContentServer> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _listener is not null;
            }
        }
    }

    public int CurrentPort { get; private set; }

    public async Task StartAsync(
        int port,
        IReadOnlyDictionary<string, string> pages,
        CancellationToken cancellationToken = default)
        => await StartAsync(port, pages, new Dictionary<string, string>(StringComparer.Ordinal), cancellationToken)
            .ConfigureAwait(false);

    public async Task StartAsync(
        int port,
        IReadOnlyDictionary<string, string> pages,
        IReadOnlyDictionary<string, string> assetRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(assetRoots);
        if (pages.Count == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pages));
        }

        var encodedPages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            encodedPages[NormalizeRoute(page.Key)] = Encoding.UTF8.GetBytes(page.Value);
        }

        var normalizedAssetRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assetRoot in assetRoots)
        {
            var routePrefix = NormalizeRoute(assetRoot.Key).TrimEnd('/');
            if (routePrefix.Length == 0)
            {
                routePrefix = "/";
            }
            normalizedAssetRoots[routePrefix] = Path.GetFullPath(assetRoot.Value);
        }

        lock (_gate)
        {
            if (_listener is not null && CurrentPort == port)
            {
                _pages = encodedPages;
                _assetRoots = normalizedAssetRoots;
                LogUpdated(_logger, port, pages.Count, assetRoots.Count);
                return;
            }
        }

        await StopAsync(cancellationToken).ConfigureAwait(false);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var cts = new CancellationTokenSource();

        lock (_gate)
        {
            _pages = encodedPages;
            _assetRoots = normalizedAssetRoots;
            _listener = listener;
            _cts = cts;
            CurrentPort = port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, cts.Token), CancellationToken.None);
        }
        LogStarted(_logger, port, pages.Count, assetRoots.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? acceptLoop;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            acceptLoop = _acceptLoop;
            _listener = null;
            _cts = null;
            _acceptLoop = null;
            _pages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _assetRoots = new Dictionary<string, string>(StringComparer.Ordinal);
            CurrentPort = 0;
        }

        if (listener is null)
        {
            return;
        }

        cts?.Cancel();
        listener.Stop();
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Expected while stopping the listener.
            }
        }
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var tcpClient = client;
        try
        {
            var stream = tcpClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            if (!await ReadRequestHeadersAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad Request"), includeBody: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            var method = parts[0];
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Method Not Allowed"), includeBody: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            var route = NormalizeRoute(parts[1]);
            byte[]? body;
            lock (_gate)
            {
                _pages.TryGetValue(route, out body);
            }

            if (body is null)
            {
                if (TryResolveAssetPath(route, out var assetPath))
                {
                    var assetBody = await File.ReadAllBytesAsync(assetPath, cancellationToken).ConfigureAwait(false);
                    await WriteResponseAsync(
                        stream,
                        200,
                        "OK",
                        ResolveContentType(assetPath),
                        assetBody,
                        includeBody: !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase),
                        cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), includeBody: !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", body, includeBody: !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
        {
            LogRequestFailed(_logger, ex.Message);
        }
    }

    private static async Task<bool> ReadRequestHeadersAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } headerLine)
        {
            if (headerLine.Length == 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveAssetPath(string route, out string assetPath)
    {
        Dictionary<string, string> roots;
        lock (_gate)
        {
            roots = new Dictionary<string, string>(_assetRoots, StringComparer.Ordinal);
        }

        foreach (var (prefix, root) in roots)
        {
            if (!route.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                continue;
            }

            var relativeRoute = route[(prefix.Length + 1)..];
            if (TryCombineAssetPath(root, relativeRoute, out assetPath))
            {
                return File.Exists(assetPath);
            }
        }

        assetPath = string.Empty;
        return false;
    }

    private static bool TryCombineAssetPath(string root, string relativeRoute, out string assetPath)
    {
        var current = Path.GetFullPath(root);
        foreach (var segment in relativeRoute.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Length == 0
                || decoded.Equals(".", StringComparison.Ordinal)
                || decoded.Equals("..", StringComparison.Ordinal)
                || decoded.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || decoded.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                assetPath = string.Empty;
                return false;
            }
            current = Path.Combine(current, decoded);
        }

        var fullPath = Path.GetFullPath(current);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(rootWithSeparator);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            assetPath = string.Empty;
            return false;
        }

        assetPath = fullPath;
        return true;
    }

    private static string ResolveContentType(string path)
    {
        var mediaType = MediaTypeMap.GetMediaType(path) ?? "application/octet-stream";
        return string.Equals(mediaType, "image/svg+xml", StringComparison.Ordinal)
            ? "image/svg+xml; charset=utf-8"
            : mediaType;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string contentType,
        byte[] body,
        bool includeBody,
        CancellationToken cancellationToken)
    {
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        if (includeBody)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string NormalizeRoute(string route)
    {
        var withoutQuery = route.Split('?', 2)[0].Trim();
        if (withoutQuery.Length == 0)
        {
            return "/";
        }
        return withoutQuery[0] == '/' ? withoutQuery : "/" + withoutQuery;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Local preview content server started on port {Port} with {PageCount} pages and {AssetRootCount} asset roots.")]
    private static partial void LogStarted(ILogger logger, int port, int pageCount, int assetRootCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Local preview content server request failed: {Message}")]
    private static partial void LogRequestFailed(ILogger logger, string message);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Local preview content server updated on port {Port} with {PageCount} pages and {AssetRootCount} asset roots.")]
    private static partial void LogUpdated(ILogger logger, int port, int pageCount, int assetRootCount);
}