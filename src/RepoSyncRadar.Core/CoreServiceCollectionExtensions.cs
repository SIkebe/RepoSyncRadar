using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services;
using RepoSyncRadar.Core.Services.Docs;
using RepoSyncRadar.Core.Services.GitHub;
using RepoSyncRadar.Core.Services.Preview;

namespace RepoSyncRadar.Core;

/// <summary>
/// One-stop DI registration for the Core layer. The host project (WPF App) is the only caller.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddRepoSyncRadarCore(this IServiceCollection services)
    {
        services.AddRepoSyncRadarOptions();

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContextFactory<RadarDbContext>((sp, options) =>
        {
            var dbPath = ResolveDbPath();
            options.UseSqlite($"Data Source={dbPath}");
        });

        services.AddHttpClient<IDocsApiClient, DocsApiClient>();

        // DocsGitHubClient is a thin Octokit wrapper. The host is expected to register
        // IGitHubClient with the appropriate credentials (see Step 20 in
        // docs/IMPLEMENTATION_PLAN.md). Resolving IDocsGitHubClient before that
        // registration will throw at first use, not at host start.
        services.TryAddSingleton<IRadarRepository, RadarRepository>();
        services.TryAddSingleton<IRadarQueryRunner, SqliteRadarQueryRunner>();
        services.TryAddSingleton<IDocsGitHubClient, DocsGitHubClient>();
        services.TryAddSingleton<ICommitIngestionService, CommitIngestionService>();
        services.TryAddSingleton<IPathToUrlResolver, NullPathToUrlResolver>();
        services.TryAddSingleton<IProcessRunner, SystemProcessRunner>();
        services.TryAddSingleton<DocsWorktreeManager>();
        services.TryAddSingleton<PreviewServerHost>();

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

        services.AddOptions<DocsRepositoryOptions>()
            .BindConfiguration(DocsRepositoryOptions.SectionName)
            .ValidateDataAnnotations();

        return services;
    }

    private static string ResolveDbPath()
    {
        // E2E tests set REPOSYNCRADAR_DB_PATH so the app does not write into the
        // developer's real %LOCALAPPDATA%\RepoSyncRadar\radar.db. Production code
        // paths leave the variable unset and fall back to the original location.
        var overridePath = Environment.GetEnvironmentVariable("REPOSYNCRADAR_DB_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var overrideDir = Path.GetDirectoryName(overridePath);
            if (!string.IsNullOrEmpty(overrideDir))
            {
                Directory.CreateDirectory(overrideDir);
            }
            return overridePath;
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RepoSyncRadar");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "radar.db");
    }
}
