using System.Globalization;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Maps a <c>github/docs</c> repository path to the URL path the Next.js preview
/// server expects (IMPLEMENTATION_PLAN.md §Step 19.5). Pure string-only function —
/// no frontmatter or pagelist lookup, so the result is a best-effort guess that
/// works for the dominant case (English <c>content/&lt;product&gt;/&lt;article&gt;.md</c>)
/// without requiring network access. The user can still navigate within the
/// preview once it is loaded.
/// </summary>
public static class PreviewPathMapper
{
    private const string ContentPrefix = "content/";
    private const string MarkdownExt = ".md";
    private const string IndexSegment = "/index";
    private const string DefaultLanguage = "en";

    /// <summary>
    /// Returns <c>"/{lang}/{path-without-content-prefix-and-md}"</c>, or <c>null</c>
    /// when <paramref name="repoPath"/> is not a publishable content markdown file.
    /// </summary>
    public static string? Map(string repoPath, string language = DefaultLanguage)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return null;
        }
        if (!repoPath.StartsWith(ContentPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        if (!repoPath.EndsWith(MarkdownExt, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rel = repoPath[ContentPrefix.Length..^MarkdownExt.Length];
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
        else if (rel.EndsWith(IndexSegment, StringComparison.Ordinal))
        {
            rel = rel[..^IndexSegment.Length];
        }

        var lang = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language;
        return rel.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"/{lang}")
            : string.Create(CultureInfo.InvariantCulture, $"/{lang}/{rel}");
    }
}
