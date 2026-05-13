using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// Launches the WPF app as a child process with two Chrome DevTools Protocol
/// ports (one per WebView2 instance) and tears it down on dispose.
/// </summary>
/// <remarks>
/// The app reads <c>REPOSYNCRADAR_BLAZOR_CDP_PORT</c> and
/// <c>REPOSYNCRADAR_DOCS_CDP_PORT</c> in <see cref="MainWindow"/>'s
/// constructor. We pick free TCP ports here so concurrent test runs do not
/// collide, write them into the child environment, then wait until each port
/// responds to <c>/json/version</c> before handing control back to the test.
/// </remarks>
public sealed class AppHost : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly Process _process;

    public int BlazorCdpPort { get; }

    public int DocsCdpPort { get; }

    private AppHost(Process process, int blazorPort, int docsPort)
    {
        _process = process;
        BlazorCdpPort = blazorPort;
        DocsCdpPort = docsPort;
    }

    public static async Task<AppHost> StartAsync(CancellationToken cancellationToken = default)
    {
        var exePath = ResolveAppExePath();
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(
                $"App executable not found at '{exePath}'. Build the App project first.",
                exePath);
        }

        var blazorPort = ReserveFreePort();
        var docsPort = ReserveFreePort();

        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        psi.EnvironmentVariables["REPOSYNCRADAR_BLAZOR_CDP_PORT"] = blazorPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.EnvironmentVariables["REPOSYNCRADAR_DOCS_CDP_PORT"] = docsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exePath}'.");

        try
        {
            await WaitForCdpAsync(blazorPort, StartupTimeout, cancellationToken).ConfigureAwait(false);
            await WaitForCdpAsync(docsPort, StartupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new AppHost(process, blazorPort, docsPort);
    }

    private static string ResolveAppExePath()
    {
        var meta = typeof(AppHost).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "RepoSyncRadarAppExePath", StringComparison.Ordinal));

        if (meta is { Value: { Length: > 0 } value })
        {
            return Path.GetFullPath(value);
        }

        // Fall back to a relative guess in case the assembly attribute is missing.
        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(here);
        var config = Path.GetFileName(Path.GetDirectoryName(here)!);
        var repoRoot = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "RepoSyncRadar.App", "bin", config, tfm, "RepoSyncRadar.exe");
    }

    private static int ReserveFreePort()
    {
        // Bind to port 0 to let the OS pick a free ephemeral port, then release
        // the listener so WebView2 can grab the port a moment later. The OS
        // rarely reuses the port within milliseconds, so collisions are unlikely.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForCdpAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var resp = await http.GetAsync(
                    new Uri($"http://127.0.0.1:{port}/json/version"),
                    cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    return;
                }
                last = new HttpRequestException($"/json/version returned {(int)resp.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException)
            {
                last = ex;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"WebView2 CDP endpoint on port {port} did not become ready within {timeout.TotalSeconds:F0}s.",
            last);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup. The dispose path will surface a real failure.
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(ShutdownTimeout);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Process did not exit within the grace window. Nothing more we can
            // do safely from here, but the test run is still ending.
        }
        catch (InvalidOperationException)
        {
            // Process was never started successfully.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
