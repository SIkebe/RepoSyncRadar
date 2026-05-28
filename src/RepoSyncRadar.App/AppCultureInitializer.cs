using System.Runtime.CompilerServices;

namespace RepoSyncRadar.App;

/// <summary>
/// Locks the process default culture to the app's neutral culture (<c>ja</c>) at
/// assembly load time, before any threads are spawned by WPF / BlazorWebView /
/// the .NET host. This ensures <see cref="Microsoft.Extensions.Localization.IStringLocalizer{T}"/>
/// resolves the neutral <c>SharedResource.resx</c> rather than the OS culture
/// (for example en-US on GitHub Actions runners), giving a deterministic first
/// render. <see cref="Components.AppHeader"/> still reapplies the user's saved
/// <c>DisplayCulture</c> once <see cref="Settings.IAppUserSettingsStore"/>
/// finishes loading.
/// </summary>
internal static class AppCultureInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        AppDisplayCulture.Apply(null);
    }
}
