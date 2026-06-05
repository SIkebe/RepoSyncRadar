namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Tracks the currently-active local preview server so that the WebView2 resource
/// filter in <c>MainWindow</c> can let <c>http://localhost:{port}/*</c> through
/// alongside the regular HTTPS allow-list.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton. <see cref="PreviewCoordinator"/> calls
/// <see cref="Activate(int[])"/> after starting the local content server, and <c>MainWindow</c>
/// checks <see cref="IsAllowed(Uri)"/> from the WebView2 <c>WebResourceRequested</c>
/// handler. Both operations are guarded by an internal lock — the writer (the
/// Razor button on the dispatcher) and the reader (the WebView2 worker) live on
/// different threads.
/// </para>
/// <para>
/// Only loopback hosts (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) on the
/// active ports are accepted; everything else is rejected so a misconfigured
/// preview server cannot accidentally widen the allow-list.
/// </para>
/// </remarks>
public sealed class PreviewSession
{
    private readonly Lock _gate = new();
    private readonly List<int> _ports = new(capacity: 2);

    /// <summary>True while a preview server is registered.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _ports.Count > 0;
            }
        }
    }

    /// <summary>The primary port assigned to the active preview server, or <c>null</c> when inactive.</summary>
    public int? ActivePort
    {
        get
        {
            lock (_gate)
            {
                return _ports.Count == 0 ? null : _ports[0];
            }
        }
    }

    /// <summary>All loopback preview ports currently allowed through WebView2 filtering.</summary>
    public IReadOnlyList<int> ActivePorts
    {
        get
        {
            lock (_gate)
            {
                return _ports.ToArray();
            }
        }
    }

    /// <summary>Marks the supplied ports as the active preview endpoints.</summary>
    public void Activate(params int[] ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        if (ports.Length == 0)
        {
            throw new ArgumentException("At least one preview port is required.", nameof(ports));
        }

        foreach (var port in ports)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        }

        lock (_gate)
        {
            _ports.Clear();
            foreach (var port in ports)
            {
                if (!_ports.Contains(port))
                {
                    _ports.Add(port);
                }
            }
        }
    }

    /// <summary>Clears the active preview. Safe to call when already inactive.</summary>
    public void Deactivate()
    {
        lock (_gate)
        {
            _ports.Clear();
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="uri"/> points at the active preview
    /// (HTTP, loopback host, matching port). All other inputs return <c>false</c>.
    /// </summary>
    public bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        int[] ports;
        lock (_gate)
        {
            ports = _ports.ToArray();
        }
        if (ports.Length == 0)
        {
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            return false;
        }
        if (!ports.Contains(uri.Port))
        {
            return false;
        }
        return uri.IsLoopback;
    }
}
