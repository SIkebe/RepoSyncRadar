using System.Reflection;
using Velopack.Locators;

namespace RepoSyncRadar.App;

/// <summary>
/// Exposes the running application's display version so the UI can surface it
/// (e.g. in the header). Prefers the Velopack-installed semantic version when
/// available so installed builds report the same version Velopack used to pack
/// the release; otherwise falls back to the entry assembly's informational
/// version (or assembly version) for dev/non-installed runs.
/// </summary>
public interface IAppVersionProvider
{
    /// <summary>
    /// Human-readable version string without a leading "v" prefix (e.g. "1.2.3"
    /// or "1.2.3-beta.4"). Never null or empty.
    /// </summary>
    string DisplayVersion { get; }
}

public sealed class AppVersionProvider : IAppVersionProvider
{
    private static readonly string CachedDisplayVersion = ResolveDisplayVersion();

    public string DisplayVersion => CachedDisplayVersion;

    private static string ResolveDisplayVersion()
    {
        // Velopack publishes the installed semantic version on the locator after
        // VelopackApp.Build().Run() has executed in Program.Main. When the app is
        // run outside an installed Velopack package (dev/F5, tests) IsCurrentSet
        // is false, in which case we fall back to the assembly version.
        try
        {
            if (VelopackLocator.IsCurrentSet)
            {
                var installed = VelopackLocator.Current?.CurrentlyInstalledVersion;
                if (installed is not null)
                {
                    return installed.ToString();
                }
            }
        }
        catch
        {
            // Fall through to assembly metadata. Velopack should not throw here,
            // but the version display must never break the UI.
        }

        var asm = Assembly.GetEntryAssembly() ?? typeof(AppVersionProvider).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // AssemblyInformationalVersion often carries a "+<commitSha>" build
            // metadata suffix from SourceLink. Strip it so the chip stays short.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
