using System.Text;

namespace RepoSyncRadar.Core.Services.Frontmatter;

/// <summary>
/// Captured frontmatter fields from a <c>github/docs</c> content file. Only the
/// <c>versions:</c> block is preserved verbatim — the rest of the YAML is intentionally ignored
/// to keep the parser dependency-free.
/// </summary>
public sealed record FrontmatterData(string VersionsRaw);

/// <summary>
/// Minimal YAML frontmatter extractor. Recognises the leading <c>---</c> fenced block at the
/// top of a Markdown file and surfaces the <c>versions:</c> sub-block as raw text. Anything
/// else is dropped — the resolver does not need it.
/// </summary>
/// <remarks>
/// We deliberately avoid pulling in a YAML library: <c>github/docs</c> frontmatter is
/// hand-authored and the <c>versions:</c> grammar we care about is a small, well-known subset
/// (<c>fpt</c>, <c>ghec</c>, <c>ghes</c> with comparator strings). A bespoke scanner keeps the
/// dependency surface small and makes the failure modes obvious.
/// </remarks>
public static class FrontmatterParser
{
    private const string _fence = "---";
    private const string _versionsKey = "versions:";

    /// <summary>
    /// Parses a Markdown source string. Returns <see langword="null"/> if the document has no
    /// frontmatter block; throws <see cref="FormatException"/> if the opening <c>---</c> is not
    /// followed by a matching closing fence.
    /// </summary>
    public static FrontmatterData? Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length == 0)
        {
            return null;
        }

        var lines = source.Split('\n');
        if (lines.Length == 0 || NormalizeLine(lines[0]) != _fence)
        {
            return null;
        }

        var closingIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (NormalizeLine(lines[i]) == _fence)
            {
                closingIndex = i;
                break;
            }
        }

        if (closingIndex < 0)
        {
            throw new FormatException(
                "Frontmatter opens with '---' but the closing fence was not found.");
        }

        var versionsRaw = ExtractVersionsBlock(lines, start: 1, end: closingIndex);
        return new FrontmatterData(versionsRaw);
    }

    private static string ExtractVersionsBlock(string[] lines, int start, int end)
    {
        var versionsLine = -1;
        for (var i = start; i < end; i++)
        {
            var line = NormalizeLine(lines[i]);
            if (line.StartsWith(_versionsKey, StringComparison.Ordinal))
            {
                versionsLine = i;
                break;
            }
        }

        if (versionsLine < 0)
        {
            return string.Empty;
        }

        var header = NormalizeLine(lines[versionsLine]);
        var afterColon = header[_versionsKey.Length..].Trim();
        if (afterColon.Length > 0)
        {
            // Inline value such as `versions: '*'`. Surface it as a single-line block so the
            // resolver's line scanner can still see it.
            return afterColon + "\n";
        }

        var builder = new StringBuilder();
        for (var i = versionsLine + 1; i < end; i++)
        {
            var line = NormalizeLine(lines[i]);
            if (line.Length == 0)
            {
                builder.Append('\n');
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                builder.Append(line);
                builder.Append('\n');
                continue;
            }

            // Next top-level key — the versions block has ended.
            break;
        }

        return builder.ToString();
    }

    private static string NormalizeLine(string line) =>
        line.EndsWith('\r') ? line[..^1] : line;
}
