using RepoSyncRadar.App.Settings;

namespace RepoSyncRadar.App.Copilot;

public sealed class CopilotUrlPermissionSettingsUpdater
{
    private readonly ILocalAppSettingsStore _settingsStore;
    private readonly UrlAllowList _allowList;

    public CopilotUrlPermissionSettingsUpdater(
        ILocalAppSettingsStore settingsStore,
        UrlAllowList allowList)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(allowList);

        _settingsStore = settingsStore;
        _allowList = allowList;
    }

    public static bool TryGetPersistableHost(string? url, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown)
        {
            return false;
        }

        host = uri.Host.ToLowerInvariant();
        return true;
    }

    public async Task<bool> AddHostFromUrlAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetPersistableHost(url, out var host))
        {
            return false;
        }

        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Copilot.AllowedUrlHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            settings.Copilot.AllowedUrlHosts.Add(host);
            await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        _allowList.AddHost(host);
        return true;
    }
}