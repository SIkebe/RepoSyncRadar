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
    private static readonly TimeSpan _startupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Environment overrides that force the optional preview pipeline OFF and keep
    /// startup auth non-interactive for any fixture that does not explicitly need
    /// either behavior. A developer's
    /// <c>appsettings.Local.json</c> usually populates the <c>DocsRepository</c>
    /// section with a real <c>github/docs</c> bare clone path, which would let an
    /// accidental click on "ローカルプレビュー" shell out to <c>git clone --bare</c>
    /// (1-2 min cold) or fail outright on CI runners without <c>git</c>. The App
    /// composes configuration in the order JSON → JSON Local → env vars
    /// (<c>RADAR_</c> prefix), so these trailing entries win and flip
    /// <c>DocsWorktreeManager.IsEnabled</c> to <c>false</c>. The fake
    /// <c>COPILOT_GITHUB_TOKEN</c> value exercises the
    /// debug-token branch of startup sign-in so CI does not enter OAuth Device Flow
    /// before Blazor has rendered.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> PreviewDisabledEnvironment { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RADAR_DocsRepository__BareCloneDir"] = string.Empty,
            ["RADAR_DocsRepository__CloneUrl"] = string.Empty,
            ["RADAR_DocsRepository__WorktreeRoot"] = string.Empty,
            ["COPILOT_GITHUB_TOKEN"] = "ghu_e2e_startup_auth_placeholder",
        };

    private readonly Process _process;
    private readonly string? _webViewUserDataRoot;

    public int BlazorCdpPort { get; }

    public int DocsCdpPort { get; }

    private AppHost(Process process, int blazorPort, int docsPort, string? webViewUserDataRoot)
    {
        _process = process;
        _webViewUserDataRoot = webViewUserDataRoot;
        BlazorCdpPort = blazorPort;
        DocsCdpPort = docsPort;
    }

    public static async Task<AppHost> StartAsync(CancellationToken cancellationToken = default)
        => await StartAsync(dbPath: null, environment: null, cancellationToken).ConfigureAwait(false);

    public static async Task<AppHost> StartAsync(string? dbPath, CancellationToken cancellationToken = default)
        => await StartAsync(dbPath, environment: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Starts the App with an optional override for the SQLite database path and an
    /// optional set of environment variables. Tests that need to seed deterministic
    /// Commits/Scoring/Drafts (e.g. to verify that the Score panel and draft
    /// sections render) point <paramref name="dbPath"/> at a throwaway file
    /// under <c>Path.GetTempPath()</c> so the developer's real
    /// <c>%LOCALAPPDATA%\RepoSyncRadar\radar.db</c> is left untouched. Pass
    /// <paramref name="environment"/> to inject <c>RADAR_*</c> overrides that flip
    /// configuration sections (e.g. force <c>DocsRepository</c> empty so the preview
    /// pipeline stays disabled during E2E runs).
    /// </summary>
    public static async Task<AppHost> StartAsync(
        string? dbPath,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken = default)
    {
        var exePath = ResolveAppExePath();
        if (!File.Exists(exePath))
        {
            var overridePath = Environment.GetEnvironmentVariable("REPOSYNCRADAR_E2E_APP_EXE_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                throw new FileNotFoundException(
                    $"App executable not found at '{exePath}'. REPOSYNCRADAR_E2E_APP_EXE_PATH is set to '{overridePath}'; verify it points to an installed RepoSyncRadar.exe or clear the override.",
                    exePath);
            }

            throw new FileNotFoundException(
                $"App executable not found at '{exePath}'. Build the App project first.",
                exePath);
        }

        var blazorPort = ReserveFreePort();
        var docsPort = ReserveFreePort();
        var webViewUserDataRoot = Path.Combine(
            Path.GetTempPath(),
            "RepoSyncRadar-WebView2-E2E-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webViewUserDataRoot);

        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        psi.EnvironmentVariables["REPOSYNCRADAR_BLAZOR_CDP_PORT"] = blazorPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.EnvironmentVariables["REPOSYNCRADAR_DOCS_CDP_PORT"] = docsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.EnvironmentVariables["REPOSYNCRADAR_WEBVIEW_USER_DATA_ROOT"] = webViewUserDataRoot;
        psi.EnvironmentVariables["REPOSYNCRADAR_USER_SETTINGS_PATH"] = Path.Combine(webViewUserDataRoot, "settings.json");
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            psi.EnvironmentVariables["REPOSYNCRADAR_DB_PATH"] = dbPath;
        }
        if (environment is not null)
        {
            foreach (var kv in environment)
            {
                // Null values clear the variable so callers can also use this to
                // explicitly unset something inherited from the parent process.
                psi.EnvironmentVariables[kv.Key] = kv.Value;
            }
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exePath}'.");

        try
        {
            await WaitForCdpAsync(blazorPort, _startupTimeout, cancellationToken).ConfigureAwait(false);
            await WaitForCdpAsync(docsPort, _startupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            TryDeleteDirectory(webViewUserDataRoot);
            throw;
        }

        return new AppHost(process, blazorPort, docsPort, webViewUserDataRoot);
    }

    internal static string ResolveAppExePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("REPOSYNCRADAR_E2E_APP_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

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
                using var cts = new CancellationTokenSource(_shutdownTimeout);
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
            TryDeleteDirectory(_webViewUserDataRoot);
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
