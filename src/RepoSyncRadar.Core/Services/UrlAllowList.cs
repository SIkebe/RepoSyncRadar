namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Host-exact, HTTPS-only allow-list used by the WebView2 surface to drop subresource
/// requests targeting hosts the operator did not explicitly approve. The check is a
/// constant-time hash lookup.
/// </summary>
/// <remarks>
/// <para>
/// "Host-exact" means that subdomains are not implicitly granted access. If the
/// operator allows <c>docs.github.com</c>, requests to <c>foo.docs.github.com</c>
/// are still blocked. This is intentional: it keeps the allow-list legible and
/// avoids accidentally widening trust to assets controlled by a different team.
/// </para>
/// <para>
/// Non-HTTPS schemes (including <c>file://</c>, <c>about:</c>, <c>http://</c>) are
/// rejected unconditionally. Garbage / unparseable inputs return <c>false</c> rather
/// than throwing so callers can use this directly inside a WebView2
/// <c>WebResourceRequested</c> handler without wrapping every call in try/catch.
/// </para>
/// </remarks>
public sealed class UrlAllowList
{
    private readonly HashSet<string> _hosts;

    public UrlAllowList(IEnumerable<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);

        _hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            _hosts.Add(host.Trim().ToLowerInvariant());
        }
    }

    /// <summary>Returns <c>true</c> when the absolute URL is HTTPS and its host is on the list.</summary>
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

        return IsAllowed(uri);
    }

    /// <summary>Returns <c>true</c> when the URI is HTTPS and its host is on the list.</summary>
    public bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        // Uri.Host is RFC 3986 normalized to lowercase by the runtime, so no extra
        // ToLowerInvariant call is needed here.
        return _hosts.Contains(uri.Host);
    }
}
