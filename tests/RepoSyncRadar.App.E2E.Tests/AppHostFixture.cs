using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// xUnit class fixture that owns the running App process and two Playwright CDP
/// connections (one per WebView2 instance). Sharing across tests in the class
/// keeps the WPF startup cost (~3-5s) off the per-test critical path.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
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
        _host = await AppHost.StartAsync();
        _playwright = await Playwright.CreateAsync();
        _blazorBrowser = await _playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{_host.BlazorCdpPort}");
        _docsBrowser = await _playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{_host.DocsCdpPort}");
    }

    public async ValueTask DisposeAsync()
    {
        // Detach Playwright first so the connection close does not race with
        // process termination on the AppHost side.
        if (_blazorBrowser is not null)
        {
            await _blazorBrowser.CloseAsync();
        }

        if (_docsBrowser is not null)
        {
            await _docsBrowser.CloseAsync();
        }

        _playwright?.Dispose();

        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}
