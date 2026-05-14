using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// xUnit class fixture that owns the running App process and two Playwright CDP
/// connections (one per WebView2 instance). Sharing across tests in the class
/// keeps the WPF startup cost (~3-5s) off the per-test critical path.
/// </summary>
/// <remarks>
/// The fixture launches the App against a throwaway SQLite database under
/// <c>Path.GetTempPath()</c> rather than the developer's real
/// <c>%LOCALAPPDATA%\RepoSyncRadar\radar.db</c>. Without that isolation, any
/// commits cached from prior <c>Sync</c> runs leak into the empty-state tests
/// in <see cref="BlazorShellE2ETests"/> and make <c>commit-list-empty</c>
/// disappear. The fixture also injects
/// <see cref="AppHost.PreviewDisabledEnvironment"/> so a stray click on
/// "ローカルプレビュー" cannot spawn <c>git</c> against a live bare clone.
/// </remarks>
public sealed class AppHostFixture : IAsyncLifetime
{
    private string? _dbDir;
    private AppHost? _host;
    private IPlaywright? _playwright;
    private IBrowser? _blazorBrowser;
    private IBrowser? _docsBrowser;

    public AppHost Host => _host
        ?? throw new InvalidOperationException("Fixture not initialized yet.");

    public IBrowser BlazorBrowser => _blazorBrowser
        ?? throw new InvalidOperationException("Fixture not initialized yet.");

    public IBrowser DocsBrowser => _docsBrowser
        ?? throw new InvalidOperationException("Fixture not initialized yet.");

    public async ValueTask InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "RepoSyncRadar-E2E-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbDir);
        var dbPath = Path.Combine(_dbDir, "radar.db");

        _host = await AppHost.StartAsync(dbPath, AppHost.PreviewDisabledEnvironment).ConfigureAwait(false);
        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        _blazorBrowser = await _playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{_host.BlazorCdpPort}").ConfigureAwait(false);
        _docsBrowser = await _playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{_host.DocsCdpPort}").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // Detach Playwright first so the connection close does not race with
        // process termination on the AppHost side.
        if (_blazorBrowser is not null)
        {
            await _blazorBrowser.CloseAsync().ConfigureAwait(false);
        }

        if (_docsBrowser is not null)
        {
            await _docsBrowser.CloseAsync().ConfigureAwait(false);
        }

        _playwright?.Dispose();

        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }

        TryCleanupDb();
    }

    private void TryCleanupDb()
    {
        if (string.IsNullOrEmpty(_dbDir))
        {
            return;
        }
        try
        {
            if (Directory.Exists(_dbDir))
            {
                Directory.Delete(_dbDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // SQLite WAL handles may still be open momentarily; the directory is
            // in TEMP so OS cleanup is acceptable.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as IOException above.
        }
    }
}
