using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RepoSyncRadar.App.Copilot;

namespace RepoSyncRadar.App;

/// <summary>
/// DI registration for the App layer (WPF-specific). Composed by <see cref="App"/> on
/// startup. Splitting this out of <c>App.OnStartup</c> keeps the host wiring testable.
/// </summary>
public static class AppServiceCollectionExtensions
{
    public static IServiceCollection AddRepoSyncRadarApp(this IServiceCollection services)
    {
        services.TryAddSingleton<UrlAllowList>();
        services.TryAddSingleton<IPermissionPrompt, WpfPermissionPrompt>();
        services.TryAddSingleton<RadarPermissionPolicy>();
        services.TryAddSingleton<ICopilotSessionFactory, CopilotSessionFactory>();

        return services;
    }
}
