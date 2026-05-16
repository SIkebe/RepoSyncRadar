using System.Globalization;
using System.Net;
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

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed partial class LocalPreviewContentServer : ILocalPreviewContentServer, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly ILogger<LocalPreviewContentServer> _logger;
    private Dictionary<string, byte[]> _pages = new(StringComparer.Ordinal);
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
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pages));
        }

        var encodedPages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            encodedPages[NormalizeRoute(page.Key)] = Encoding.UTF8.GetBytes(page.Value);
        }

        lock (_gate)
        {
            if (_listener is not null && CurrentPort == port)
            {
                _pages = encodedPages;
                LogUpdated(_logger, port, pages.Count);
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
            _listener = listener;
            _cts = cts;
            CurrentPort = port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, cts.Token), CancellationToken.None);
        }
        LogStarted(_logger, port, pages.Count);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Local preview content server started on port {Port} with {PageCount} pages.")]
    private static partial void LogStarted(ILogger logger, int port, int pageCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Local preview content server request failed: {Message}")]
    private static partial void LogRequestFailed(ILogger logger, string message);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Local preview content server updated on port {Port} with {PageCount} pages.")]
    private static partial void LogUpdated(ILogger logger, int port, int pageCount);
}