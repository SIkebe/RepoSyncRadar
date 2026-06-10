using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Octokit;
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

        // Octokit client is registered here without credentials; DocsGitHubClient
        // refreshes Connection.Credentials on every call using the same OAuth
        // user token that the Copilot SDK consumes (IGitHubAccessTokenProvider,
        // registered by the App layer). This keeps GitHub auth on a single token.
        services.TryAddSingleton<IGitHubClient>(_ =>
            new GitHubClient(new ProductHeaderValue("RepoSyncRadar")));

        services.TryAddSingleton<IRadarRepository, RadarRepository>();
        services.TryAddSingleton<IDocsGitHubClient, DocsGitHubClient>();
        services.TryAddSingleton<ICommitIngestionService, CommitIngestionService>();
        services.TryAddSingleton<ITriagePreflightSummaryBuilder, TriagePreflightSummaryBuilder>();
        services.TryAddSingleton<IPathToUrlResolver, NullPathToUrlResolver>();
        services.TryAddSingleton<IProcessRunner, SystemProcessRunner>();
        services.TryAddSingleton<IPreviewPortAllocator, TcpPreviewPortAllocator>();
        services.TryAddSingleton<IPreviewServerProcessCleaner, NextDevServerProcessCleaner>();
        services.TryAddSingleton<ILocalPreviewContentServer, LocalPreviewContentServer>();
        services.TryAddSingleton<DocsWorktreeManager>();
        services.TryAddSingleton<PreviewSession>();
        services.TryAddSingleton<IPreviewCoordinator, PreviewCoordinator>();

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
            .Validate<DocsApiOptionsValidator>()
            .ValidateOnStart();

        services.AddOptions<CopilotOptions>()
            .BindConfiguration(CopilotOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<CopilotOptions>, CopilotOptionsPostConfigurer>();

        services.AddOptions<WebViewOptions>()
            .BindConfiguration(WebViewOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<WebViewOptions>, WebViewOptionsPostConfigurer>();

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
