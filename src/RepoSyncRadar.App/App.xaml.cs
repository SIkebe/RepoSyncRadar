using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using RepoSyncRadar.Core;
using RepoSyncRadar.Core.Data;

namespace RepoSyncRadar.App;

/// <summary>
/// Application entry point. Sets up the generic host, DI container, and shows <see cref="MainWindow"/>.
/// </summary>
public partial class App : Application
{
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
