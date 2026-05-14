namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Tracks the currently-active local preview server so that the WebView2 resource
/// filter in <c>MainWindow</c> can let <c>http://localhost:{port}/*</c> through
/// alongside the regular HTTPS allow-list (IMPLEMENTATION_PLAN.md §Step 19.5).
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton. <see cref="PreviewCoordinator"/> calls
/// <see cref="Activate(int)"/> after starting the sidecar, and <c>MainWindow</c>
/// checks <see cref="IsAllowed(Uri)"/> from the WebView2 <c>WebResourceRequested</c>
/// handler. Both operations are guarded by an internal lock — the writer (the
/// Razor button on the dispatcher) and the reader (the WebView2 worker) live on
/// different threads.
/// </para>
/// <para>
/// Only loopback hosts (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) on the
/// active port are accepted; everything else is rejected so a misconfigured
/// preview server cannot accidentally widen the allow-list.
/// </para>
/// </remarks>
public sealed class PreviewSession
{
    private readonly Lock _gate = new();
    private int? _port;

    /// <summary>True while a preview server is registered.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _port.HasValue;
            }
        }
    }

    /// <summary>The port assigned to the active preview server, or <c>null</c> when inactive.</summary>
    public int? ActivePort
    {
        get
        {
            lock (_gate)
            {
                return _port;
            }
        }
    }

    /// <summary>Marks the supplied port as the active preview.</summary>
    public void Activate(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        lock (_gate)
        {
            _port = port;
        }
    }

    /// <summary>Clears the active preview. Safe to call when already inactive.</summary>
    public void Deactivate()
    {
        lock (_gate)
        {
            _port = null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="uri"/> points at the active preview
    /// (HTTP, loopback host, matching port). All other inputs return <c>false</c>.
    /// </summary>
    public bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        int? port;
        lock (_gate)
        {
            port = _port;
        }
        if (port is null)
        {
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            return false;
        }
        if (uri.Port != port.Value)
        {
            return false;
        }
        return uri.IsLoopback;
    }
}
