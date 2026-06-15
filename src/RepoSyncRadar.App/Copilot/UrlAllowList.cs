using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Compiled allow-list for the <c>url</c> permission kind. Hosts are sourced from
/// <see cref="CopilotOptions.AllowedUrlHosts"/> and compared case-insensitively. Non-HTTPS
/// URLs are never allowed without prompting.
/// </summary>
public sealed class UrlAllowList
{
    private readonly object _gate = new();
    private readonly HashSet<string> _hosts;

    public UrlAllowList(IOptions<CopilotOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in options.Value.AllowedUrlHosts)
        {
            AddHost(host);
        }
    }

    public void AddHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        lock (_gate)
        {
            _hosts.Add(host.Trim());
        }
    }

    public bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_gate)
        {
            return _hosts.Contains(uri.Host);
        }
    }
}
