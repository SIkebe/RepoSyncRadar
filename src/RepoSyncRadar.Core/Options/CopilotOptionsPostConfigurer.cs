using Microsoft.Extensions.Options;

namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Normalizes <see cref="CopilotOptions.AllowedUrlHosts"/> to lowercase and removes
/// duplicates so downstream allow-list checks can do a plain ordinal comparison.
/// </summary>
internal sealed class CopilotOptionsPostConfigurer : IPostConfigureOptions<CopilotOptions>
{
    public void PostConfigure(string? name, CopilotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.AllowedUrlHosts is null)
        {
            options.AllowedUrlHosts = [];
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(options.AllowedUrlHosts.Count);
        foreach (var host in options.AllowedUrlHosts)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            var lower = host.Trim().ToLowerInvariant();
            if (seen.Add(lower))
            {
                normalized.Add(lower);
            }
        }

        options.AllowedUrlHosts = normalized;
    }
}
