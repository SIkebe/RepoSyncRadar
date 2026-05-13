using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.App.Copilot.Audit;
using RepoSyncRadar.App.Copilot.Tools;
using RepoSyncRadar.Core.Services;

namespace RepoSyncRadar.App;

/// <summary>
/// DI registration for the App layer (WPF-specific). Composed by <see cref="App"/> on
/// startup. Splitting this out of <c>App.OnStartup</c> keeps the host wiring testable.
/// </summary>
public static class AppServiceCollectionExtensions
{
    public static IServiceCollection AddRepoSyncRadarApp(this IServiceCollection services)
    {
        services.TryAddSingleton<RepoSyncRadar.App.Copilot.UrlAllowList>();
        services.TryAddSingleton<IPermissionPrompt, WpfPermissionPrompt>();
        services.TryAddSingleton<RadarPermissionPolicy>();
        services.TryAddSingleton<IAuditJsonlSink>(sp =>
            FileSystemAuditJsonlSink.CreateDefault(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ToolAuditHook>();
        services.TryAddSingleton<RadarTools>();
        services.TryAddSingleton<RadarWriteTools>();
        services.TryAddSingleton<ICopilotSessionFactory, CopilotSessionFactory>();
        services.TryAddSingleton<MorningTriageSession>();
        services.TryAddSingleton<AdoptionSession>();
        services.TryAddSingleton<ICopilotAgent, CopilotAgent>();
        services.TryAddSingleton<IReviewBroadcaster, ReviewBroadcaster>();
        services.TryAddSingleton<IClipboard, WpfClipboard>();

        return services;
    }
}
