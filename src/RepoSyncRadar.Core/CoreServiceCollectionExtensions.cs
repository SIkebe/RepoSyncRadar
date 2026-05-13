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
        services.AddRepoSyncRadarOptions();

        services.AddDbContextFactory<RadarDbContext>((sp, options) =>
        {
            var dbPath = ResolveDbPath();
            options.UseSqlite($"Data Source={dbPath}");
        });

        return services;
    }

    /// <summary>
    /// Registers and validates the <c>GitHub</c>, <c>DocsApi</c>, and <c>Copilot</c> options
    /// sections. Validation runs at <c>IHost.StartAsync</c> time so that an invalid
    /// <c>appsettings.json</c> fails fast instead of silently producing default values.
    /// </summary>
    public static IServiceCollection AddRepoSyncRadarOptions(this IServiceCollection services)
    {
        services.AddOptions<GitHubOptions>()
            .BindConfiguration(GitHubOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DocsApiOptions>()
            .BindConfiguration(DocsApiOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DocsApiOptions>, DocsApiOptionsValidator>();

        services.AddOptions<CopilotOptions>()
            .BindConfiguration(CopilotOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<CopilotOptions>, CopilotOptionsPostConfigurer>();

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
