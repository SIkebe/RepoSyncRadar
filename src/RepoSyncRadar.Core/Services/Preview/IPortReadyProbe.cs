using System.Net.Sockets;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Polls a loopback TCP port until something accepts a connection. Used by
/// <see cref="PreviewServerHost"/> to decide when the docs preview server (e.g.
/// the <c>github/docs</c> <c>nodemon src/frame/server.ts</c>) has finished its
/// cold start and is actually ready to answer requests; otherwise the WebView2
/// host navigates too early and the user just sees "接続できません".
/// </summary>
public interface IPortReadyProbe
{
    /// <summary>
    /// Waits until <paramref name="port"/> on <c>127.0.0.1</c> accepts a TCP
    /// connection.
    /// </summary>
    /// <param name="port">Loopback TCP port to probe.</param>
    /// <param name="timeout">Hard cap. Returns <c>false</c> when this elapses.</param>
    /// <param name="processStillAlive">Optional liveness check. Returning <c>false</c>
    /// aborts the wait immediately (e.g. the child exited).</param>
    /// <param name="cancellationToken">User cancellation.</param>
    /// <returns><c>true</c> when the port is accepting connections; <c>false</c>
    /// when <paramref name="timeout"/> elapsed or <paramref name="processStillAlive"/>
    /// returned <c>false</c>.</returns>
    Task<bool> WaitForListenAsync(
        int port,
        TimeSpan timeout,
        Func<bool>? processStillAlive,
        CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IPortReadyProbe"/> backed by <see cref="TcpClient"/>.</summary>
public sealed class TcpPortReadyProbe : IPortReadyProbe
{
    /// <summary>Interval between probe attempts. Kept small enough to be responsive but
    /// large enough not to drown the logger.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Per-attempt connect timeout so a misbehaving stack can't pin the probe.</summary>
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(2);

    public async Task<bool> WaitForListenAsync(
        int port,
        TimeSpan timeout,
        Func<bool>? processStillAlive,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(port, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout.Ticks, 0);

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            if (processStillAlive is not null && !processStillAlive())
            {
                return false;
            }
            if (await TryConnectAsync(port, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    private static async Task<bool> TryConnectAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(ConnectAttemptTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await client.ConnectAsync("127.0.0.1", port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
