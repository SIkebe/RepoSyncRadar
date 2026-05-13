using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core;

/// <summary>
/// One-stop DI registration for the Core layer. The host project (WPF App) is the only caller.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddRepoSyncRadarCore(this IServiceCollection services)
    {
        services.AddOptions<GitHubOptions>().BindConfiguration(GitHubOptions.SectionName);
        services.AddOptions<DocsApiOptions>().BindConfiguration(DocsApiOptions.SectionName);
        services.AddOptions<CopilotOptions>().BindConfiguration(CopilotOptions.SectionName);

        services.AddDbContextFactory<RadarDbContext>((sp, options) =>
        {
            var dbPath = ResolveDbPath();
            options.UseSqlite($"Data Source={dbPath}");
        });

        return services;
    }

    private static string ResolveDbPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RepoSyncRadar");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "radar.db");
    }
}
