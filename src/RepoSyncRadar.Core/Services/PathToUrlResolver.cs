using System.Globalization;

namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Resolves a <c>github/docs</c> repository path (e.g. <c>content/copilot/about-copilot.md</c>)
/// to one or more canonical <c>docs.github.com</c> URLs by intersecting the frontmatter
/// <c>versions:</c> declaration with a caller-supplied pagelist snapshot.
/// </summary>
/// <remarks>
/// <para>
/// This type is intentionally a pure function: no HTTP, no DB, no caching. Callers fetch the
/// pagelist via <c>IDocsApiClient</c> (Step 5) and hand the result in. That makes the resolver
/// trivially unit-testable and lets the caching layer evolve independently.
/// </para>
/// <para>
/// The pagelist dictionary is keyed by <c>"&lt;lang&gt;/&lt;versionId&gt;"</c>
/// (e.g. <c>"en/fpt"</c>, <c>"en/ghes-3.14"</c>). If the requested language has no pagelist
/// the resolver falls back to <c>en</c>, matching the upstream behaviour for missing
/// translations.
/// </para>
/// </remarks>
public static class PathToUrlResolver
{
    private const string ContentPrefix = "content/";
    private const string MarkdownExt = ".md";
    private const string GhesPrefix = "ghes-";
    private const string EnglishLanguage = "en";

    /// <summary>
    /// Returns all canonical URLs for the given repository path. An empty list means
    /// "the path is not a publishable article" (e.g. <c>data/release-notes/...</c>), the
    /// frontmatter could not be parsed, or no pagelist entry matched.
    /// </summary>
    /// <param name="repoPath">Repository-relative path such as <c>content/copilot/about-copilot.md</c>.</param>
    /// <param name="frontmatterVersions">Raw text of the <c>versions:</c> YAML block.</param>
    /// <param name="pageListByLangVersion">
    /// Pagelist snapshot keyed by <c>"&lt;lang&gt;/&lt;versionId&gt;"</c>.
    /// </param>
    /// <param name="language">Preferred UI language. Defaults to <c>en</c>.</param>
    public static IReadOnlyList<string> Resolve(
        string repoPath,
        string frontmatterVersions,
        IReadOnlyDictionary<string, IReadOnlyList<string>> pageListByLangVersion,
        string language = EnglishLanguage)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        ArgumentNullException.ThrowIfNull(frontmatterVersions);
        ArgumentNullException.ThrowIfNull(pageListByLangVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        if (!repoPath.StartsWith(ContentPrefix, StringComparison.Ordinal))
        {
            return [];
        }

        var rel = repoPath[ContentPrefix.Length..];
        if (rel.EndsWith(MarkdownExt, StringComparison.OrdinalIgnoreCase))
        {
            rel = rel[..^MarkdownExt.Length];
        }

        if (rel.Length == 0)
        {
            return [];
        }

        var suffix = "/" + rel;
        var versions = ParseVersions(frontmatterVersions, pageListByLangVersion, language);
        if (versions.Count == 0)
        {
            return [];
        }

        var results = new List<string>(versions.Count);
        foreach (var versionId in versions)
        {
            var pages = LookupPageList(pageListByLangVersion, language, versionId);
            if (pages is null)
            {
                continue;
            }

            foreach (var entry in pages)
            {
                if (entry.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                    break;
                }
            }
        }

        return results;
    }

    private static IReadOnlyList<string>? LookupPageList(
        IReadOnlyDictionary<string, IReadOnlyList<string>> pageListByLangVersion,
        string language,
        string versionId)
    {
        if (pageListByLangVersion.TryGetValue($"{language}/{versionId}", out var pages))
        {
            return pages;
        }

        if (!string.Equals(language, EnglishLanguage, StringComparison.Ordinal)
            && pageListByLangVersion.TryGetValue($"{EnglishLanguage}/{versionId}", out var fallback))
        {
            return fallback;
        }

        return null;
    }

    private static List<string> ParseVersions(
        string frontmatterVersions,
        IReadOnlyDictionary<string, IReadOnlyList<string>> pageListByLangVersion,
        string language)
    {
        var resolved = new List<string>();
        if (string.IsNullOrWhiteSpace(frontmatterVersions))
        {
            return resolved;
        }

        foreach (var rawLine in frontmatterVersions.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var product = line[..colon].Trim();
            var value = StripQuotes(line[(colon + 1)..].Trim());

            switch (product)
            {
                case "fpt":
                case "ghec":
                    if (IsWildcard(value))
                    {
                        resolved.Add(product);
                    }
                    break;

                case "ghes":
                    AppendGhesVersions(resolved, value, pageListByLangVersion, language);
                    break;
            }
        }

        return resolved;
    }

    private static void AppendGhesVersions(
        List<string> resolved,
        string spec,
        IReadOnlyDictionary<string, IReadOnlyList<string>> pageListByLangVersion,
        string language)
    {
        var available = EnumerateAvailableGhesVersions(pageListByLangVersion, language).ToList();
        if (available.Count == 0)
        {
            return;
        }

        if (IsWildcard(spec))
        {
            foreach (var version in available)
            {
                resolved.Add(GhesPrefix + FormatVersion(version));
            }
            return;
        }

        if (!TryParseComparator(spec, out var op, out var target))
        {
            return;
        }

        foreach (var version in available)
        {
            if (SatisfiesComparator(version, op, target))
            {
                resolved.Add(GhesPrefix + FormatVersion(version));
            }
        }
    }

    private static IEnumerable<Version> EnumerateAvailableGhesVersions(
        IReadOnlyDictionary<string, IReadOnlyList<string>> pageListByLangVersion,
        string language)
    {
        var primaryPrefix = $"{language}/{GhesPrefix}";
        var fallbackPrefix = $"{EnglishLanguage}/{GhesPrefix}";

        var seen = new HashSet<Version>();
        foreach (var key in pageListByLangVersion.Keys)
        {
            string? versionPart = null;
            if (key.StartsWith(primaryPrefix, StringComparison.Ordinal))
            {
                versionPart = key[primaryPrefix.Length..];
            }
            else if (!string.Equals(language, EnglishLanguage, StringComparison.Ordinal)
                && key.StartsWith(fallbackPrefix, StringComparison.Ordinal))
            {
                versionPart = key[fallbackPrefix.Length..];
            }

            if (versionPart is null)
            {
                continue;
            }

            if (Version.TryParse(versionPart, out var parsed) && seen.Add(parsed))
            {
                yield return parsed;
            }
        }
    }

    private static bool TryParseComparator(string spec, out string op, out Version target)
    {
        op = string.Empty;
        target = new Version(0, 0);

        var trimmed = spec.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<string> operators = ["<=", ">=", "<", ">", "="];
        foreach (var candidate in operators)
        {
            if (trimmed.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                var rest = trimmed[candidate.Length..].Trim();
                return Version.TryParse(rest, out target!);
            }
        }

        op = "=";
        return Version.TryParse(trimmed, out target!);
    }

    private static bool SatisfiesComparator(Version version, string op, Version target) => op switch
    {
        ">=" => version >= target,
        "<=" => version <= target,
        ">" => version > target,
        "<" => version < target,
        "=" => version == target,
        _ => false,
    };

    private static string FormatVersion(Version version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}");

    private static bool IsWildcard(string value) => value.Length == 0 || value == "*";

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2
            && (value[0] == '\'' || value[0] == '"')
            && value[^1] == value[0])
        {
            return value[1..^1];
        }
        return value;
    }
}
