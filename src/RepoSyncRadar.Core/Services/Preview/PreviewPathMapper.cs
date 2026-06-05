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
}
