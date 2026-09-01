using System.Globalization;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Maps a <c>github/docs</c> repository path to the docs URL path used by
/// fallback navigation and preview query metadata. Pure string-only function —
/// no frontmatter or pagelist lookup, so the result is a best-effort guess that
/// works for the dominant case (English <c>content/&lt;product&gt;/&lt;article&gt;.md</c>)
/// without requiring network access. The user can still navigate within the
/// preview once it is loaded.
/// </summary>
public static class PreviewPathMapper
{
    private const string _contentPrefix = "content/";
    private const string _markdownExt = ".md";
    private const string _markdownLongExt = ".markdown";
    private const string _indexSegment = "/index";
    private const string _defaultLanguage = "en";

    /// <summary>
    /// Returns <c>"/{lang}/{path-without-content-prefix-and-md}"</c>, or <c>null</c>
    /// when <paramref name="repoPath"/> is not a publishable content markdown file.
    /// </summary>
    public static string? Map(string repoPath, string language = _defaultLanguage)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return null;
        }
        if (!repoPath.StartsWith(_contentPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        if (!repoPath.EndsWith(_markdownExt, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rel = repoPath[_contentPrefix.Length..^_markdownExt.Length];
        if (rel.Length == 0)
        {
            // "content/.md" — nonsense input.
            return null;
        }

        // "content/foo/index.md" → "/{lang}/foo", "content/index.md" → "/{lang}".
        if (rel.Equals("index", StringComparison.Ordinal))
        {
            rel = string.Empty;
        }
        else if (rel.EndsWith(_indexSegment, StringComparison.Ordinal))
        {
            rel = rel[..^_indexSegment.Length];
        }

        var lang = string.IsNullOrWhiteSpace(language) ? _defaultLanguage : language;
        return rel.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"/{lang}")
            : string.Create(CultureInfo.InvariantCulture, $"/{lang}/{rel}");
    }

    /// <summary>Returns true for repository-relative Markdown files, including non-publishable files such as CHANGELOG.md.</summary>
    public static bool IsMarkdown(string repoPath)
        => !string.IsNullOrWhiteSpace(repoPath)
            && (repoPath.EndsWith(_markdownExt, StringComparison.OrdinalIgnoreCase)
                || repoPath.EndsWith(_markdownLongExt, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns true for generated REST or GraphQL reference data that is rendered
    /// into the corresponding pages on docs.github.com.
    /// </summary>
    public static bool IsApiReferenceData(string repoPath)
        => MapApiReferenceData(repoPath) is not null;

    internal static ApiReferencePreviewDescriptor? MapApiReferenceData(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return null;
        }

        var normalized = repoPath.Replace('\\', '/').TrimStart('/');
        const string restPrefix = "src/rest/data/";
        if (normalized.StartsWith(restPrefix, StringComparison.Ordinal)
            && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var segments = normalized[restPrefix.Length..].Split('/');
            if (segments.Length != 2
                || !TryParseVersionDirectory(segments[0], out var version, out var apiVersion))
            {
                return null;
            }

            var category = segments[1][..^".json".Length];
            return IsReferenceCategory(category)
                ? new ApiReferencePreviewDescriptor(
                    ApiReferenceKind.Rest,
                    version,
                    apiVersion,
                    category,
                    $"/en/rest/{category}")
                : null;
        }

        const string graphqlPrefix = "src/graphql/data/";
        if (normalized.StartsWith(graphqlPrefix, StringComparison.Ordinal)
            && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var segments = normalized[graphqlPrefix.Length..].Split('/');
            const string schemaPrefix = "schema-";
            if (segments.Length != 2
                || !TryParseVersionDirectory(segments[0], out var version, out _)
                || !segments[1].StartsWith(schemaPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            var category = segments[1][schemaPrefix.Length..^".json".Length];
            return IsReferenceCategory(category)
                ? new ApiReferencePreviewDescriptor(
                    ApiReferenceKind.GraphQl,
                    version,
                    ApiVersion: null,
                    category,
                    $"/en/graphql/reference/{category}")
                : null;
        }

        return null;
    }

    private static bool TryParseVersionDirectory(
        string directory,
        out string version,
        out string? apiVersion)
    {
        version = string.Empty;
        apiVersion = null;
        if (directory.Equals("fpt", StringComparison.Ordinal)
            || directory.Equals("ghec", StringComparison.Ordinal))
        {
            version = directory;
            return true;
        }

        var dateSeparator = directory.LastIndexOf('-');
        if (directory.Length > 11
            && dateSeparator == directory.Length - 3
            && DateOnly.TryParseExact(
                directory[^10..],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _))
        {
            apiVersion = directory[^10..];
            directory = directory[..^11];
        }

        if (directory.Equals("fpt", StringComparison.Ordinal)
            || directory.Equals("ghec", StringComparison.Ordinal)
            || (directory.StartsWith("ghes-", StringComparison.Ordinal)
                && Version.TryParse(directory["ghes-".Length..], out _)))
        {
            version = directory;
            return true;
        }

        return false;
    }

    private static bool IsReferenceCategory(string category)
        => !string.IsNullOrWhiteSpace(category)
            && !category.Equals("schema", StringComparison.OrdinalIgnoreCase)
            && !category.Equals("category-map", StringComparison.OrdinalIgnoreCase)
            && !category.Equals("changelog", StringComparison.OrdinalIgnoreCase)
            && !category.Equals("previews", StringComparison.OrdinalIgnoreCase);
}

internal enum ApiReferenceKind
{
    Rest,
    GraphQl,
}

internal sealed record ApiReferencePreviewDescriptor(
    ApiReferenceKind Kind,
    string Version,
    string? ApiVersion,
    string Category,
    string OfficialPath);
