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
using RepoSyncRadar.Core.Services.Preview;

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

        // Install global unhandled-exception sinks BEFORE the host is built so that
        // exceptions during DI composition (or anywhere in the BlazorWebView) do not
        // tear down the WPF process. The previous behaviour was that any non-
        // InvalidOperationException raised inside the preview pipeline (Win32Exception,
        // IOException, etc.) crashed the app silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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

        // Eager docs preview prewarm. Doing `git clone --bare` (1-2 minutes for
        // github/docs) ahead of time means the user's first preview click
        // skips the slowest non-npm step. Best-effort: if the network is down
        // or the repo is misconfigured, the regular preview path will surface
        // the error when the user clicks.
        _ = PrewarmPreviewAsync(
            Services.GetRequiredService<IPreviewCoordinator>(),
            Services.GetRequiredService<ILogger<App>>());
    }

    internal static async Task PrewarmPreviewAsync(IPreviewCoordinator coordinator, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            await coordinator.PrewarmAsync(CancellationToken.None).ConfigureAwait(false);
            LogPreviewPrewarmCompleted(logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPreviewPrewarmFailed(logger, ex);
        }
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

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "Unhandled exception caught by global sink. Application kept alive; user should consider restarting.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Preview prewarm completed; first preview click should skip git clone --bare.")]
    private static partial void LogPreviewPrewarmCompleted(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Preview prewarm failed; the first preview click will fall back to the cold path.")]
    private static partial void LogPreviewPrewarmFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Last-resort handler for any exception that escapes the BlazorWebView, a
    /// fire-and-forget task, or the WPF dispatcher. Splits the WPF / dialog
    /// concerns from the logging core so unit tests can drive it without
    /// instantiating a <see cref="System.Windows.MessageBox"/>.
    /// </summary>
    /// <param name="exception">Exception to report, or <c>null</c> when the source
    /// raised a non-<see cref="Exception"/> payload (AppDomain unhandled).</param>
    /// <param name="logger">Optional logger; <c>null</c> is treated as a no-op so the
    /// sink stays callable even during DI composition failure.</param>
    /// <param name="showDialog">Optional UI callback invoked with the formatted
    /// message. Tests pass a <c>List&lt;string&gt;</c>.Add; production passes
    /// <see cref="ShowUnhandledDialog"/>.</param>
    internal static void HandleUnhandled(Exception? exception, ILogger? logger, Action<string>? showDialog)
    {
        if (exception is null)
        {
            return;
        }
        if (logger is not null)
        {
            LogUnhandled(logger, exception);
        }
        if (showDialog is null)
        {
            return;
        }
        try
        {
            var message = $"想定外のエラーが発生しました。\n\n{exception.GetType().Name}: {exception.Message}";
            showDialog(message);
        }
        catch
        {
            // Best-effort: do not throw from the global sink.
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        HandleUnhandled(e.Exception, _host?.Services.GetService<ILogger<App>>(), ShowUnhandledDialog);
        e.Handled = true; // Keep the WPF message pump alive.
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        HandleUnhandled(e.ExceptionObject as Exception, _host?.Services.GetService<ILogger<App>>(), ShowUnhandledDialog);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        HandleUnhandled(e.Exception, _host?.Services.GetService<ILogger<App>>(), ShowUnhandledDialog);
        e.SetObserved();
    }

    private static void ShowUnhandledDialog(string message)
    {
        try
        {
            MessageBox.Show(
                message,
                "RepoSyncRadar — 想定外のエラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Dispatcher may already be torn down — keep the sink quiet.
        }
    }

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
