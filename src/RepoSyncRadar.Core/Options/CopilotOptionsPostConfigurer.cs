using Microsoft.Extensions.Options;

namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Normalizes <see cref="CopilotOptions.AllowedUrlHosts"/> and
/// <see cref="CopilotOptions.OAuthScopes"/> so downstream checks can do plain ordinal
/// comparisons.
/// </summary>
internal sealed class CopilotOptionsPostConfigurer : IPostConfigureOptions<CopilotOptions>
{
    public void PostConfigure(string? name, CopilotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AllowedUrlHosts = Normalize(options.AllowedUrlHosts, lowercase: true);
        options.OAuthScopes = Normalize(options.OAuthScopes, lowercase: true);

        if (!string.IsNullOrWhiteSpace(options.OAuthClientId))
        {
            options.OAuthClientId = options.OAuthClientId.Trim();
        }
        else
        {
            options.OAuthClientId = null;
        }
    }

    internal static List<string> Normalize(IReadOnlyList<string>? source, bool lowercase)
    {
        if (source is null || source.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(source.Count);
        foreach (var item in source)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var value = item.Trim();
            if (lowercase)
            {
                value = value.ToLowerInvariant();
            }

            if (seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }
}
