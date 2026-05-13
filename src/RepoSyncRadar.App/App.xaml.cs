using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using RepoSyncRadar.Core;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App;

/// <summary>
/// Application entry point. Sets up the generic host, DI container, and shows <see cref="MainWindow"/>.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Warning shown when <c>Copilot:OAuthClientId</c> is missing on startup. Made
    /// <c>internal</c> so the test project can assert against the exact text.
    /// </summary>
    internal const string MissingClientIdWarning =
        "GitHub OAuth Client Id (Copilot:OAuthClientId) \u304c appsettings.json \u306b\u8a2d\u5b9a\u3055\u308c\u3066\u3044\u307e\u305b\u3093\u3002\n" +
        "OAuth App \u3092\u767b\u9332\u3057\u3066 Client Id \u3092\u8a2d\u5b9a\u3059\u308b\u307e\u3067\u3001Copilot \u6a5f\u80fd\u3068 Sync \u306f\u4f7f\u3048\u307e\u305b\u3093\u3002\n" +
        "\u624b\u9806\u306f docs/USAGE.md \u3092\u53c2\u7167\u3057\u3066\u304f\u3060\u3055\u3044\u3002";

    private IHost? _host;

    /// <summary>The composed DI container, shared with the BlazorWebView.</summary>
    public IServiceProvider Services => _host?.Services
        ?? throw new InvalidOperationException("Host not started yet.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(e.Args);

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("RADAR_");

        builder.Logging.AddDebug();

        builder.Services.AddWpfBlazorWebView();
        builder.Services.AddMudServices();
        builder.Services.AddRepoSyncRadarCore();
        builder.Services.AddRepoSyncRadarApp();

        _host = builder.Build();
        await _host.StartAsync();

        await MigrateDatabaseAsync(_host.Services);

        var main = new MainWindow(Services);
        MainWindow = main;
        main.Show();

        // Eager startup sign-in. Fire-and-forget so the WPF message pump keeps running
        // and the BlazorWebView renders while the device-flow dialog (or warning) is
        // shown. Failures are intentionally swallowed inside the helper; the next
        // Copilot/Sync action will retry through the same provider.
        _ = TrySignInOnStartupAsync(
            Services.GetRequiredService<IOptions<CopilotOptions>>().Value,
            Services.GetRequiredService<IGitHubAccessTokenProvider>(),
            Services.GetRequiredService<ILogger<App>>(),
            ShowStartupWarning,
            CancellationToken.None);
    }

    /// <summary>
    /// Eager startup sign-in helper. Pure logic isolated from <see cref="MessageBox"/>
    /// via the <paramref name="warnUser"/> callback so it can be unit tested in a
    /// headless test runner.
    /// </summary>
    /// <remarks>
    /// Behaviour rules:
    /// <list type="bullet">
    /// <item>If <c>OAuthClientId</c> is missing/whitespace, surface <paramref name="warnUser"/>
    /// and return without touching the provider.</item>
    /// <item>Otherwise call <see cref="IGitHubAccessTokenProvider.GetAccessTokenAsync"/>;
    /// the provider's existing logic resolves from env override -> cache -> store ->
    /// Device Flow.</item>
    /// <item>Any exception (including <see cref="OperationCanceledException"/>) is
    /// swallowed; the next Copilot/Sync operation will retry.</item>
    /// </list>
    /// </remarks>
    internal static async Task TrySignInOnStartupAsync(
        CopilotOptions options,
        IGitHubAccessTokenProvider tokenProvider,
        ILogger logger,
        Action<string> warnUser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(warnUser);

        if (string.IsNullOrWhiteSpace(options.OAuthClientId))
        {
            LogStartupClientIdMissing(logger);
            warnUser(MissingClientIdWarning);
            return;
        }

        try
        {
            _ = await tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Process shutdown or user cancelled the device-flow dialog. Drop quietly.
        }
        catch (Exception ex)
        {
            LogStartupSignInFailed(logger, ex);
        }
    }

    private static void ShowStartupWarning(string message)
    {
        MessageBox.Show(
            message,
            "RepoSyncRadar \u2014 \u8a2d\u5b9a\u304c\u5fc5\u8981",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Copilot:OAuthClientId is not configured; skipping eager startup sign-in.")]
    private static partial void LogStartupClientIdMissing(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Eager GitHub sign-in failed on startup; will retry on next Copilot action.")]
    private static partial void LogStartupSignInFailed(ILogger logger, Exception exception);

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Applies any pending EF Core migrations against the local SQLite store. Runs once
    /// per process startup so a freshly installed copy of the app gets a usable database
    /// without manual <c>dotnet ef database update</c> calls.
    /// </summary>
    private static async Task MigrateDatabaseAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RadarDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }
}
