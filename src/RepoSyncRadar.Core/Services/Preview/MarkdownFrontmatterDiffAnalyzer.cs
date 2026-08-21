namespace RepoSyncRadar.Core.Services.Preview;

public sealed record MarkdownFrontmatterChange(
    DocsVersionChangeKind Kind,
    string? BeforeLine,
    string? AfterLine);

public static class MarkdownFrontmatterDiffAnalyzer
{
    private const int MaxLcsCells = 1_000_000;

    public static IReadOnlyList<MarkdownFrontmatterChange> Analyze(string? beforeMarkdown, string? afterMarkdown)
    {
        var beforeLines = SplitFrontmatterLines(beforeMarkdown);
        var afterLines = SplitFrontmatterLines(afterMarkdown);
        if (beforeLines.Length == 0 && afterLines.Length == 0)
        {
            return Array.Empty<MarkdownFrontmatterChange>();
        }

        var table = BuildLcsTableIfBounded(beforeLines, afterLines);
        return table is null
            ? AnalyzeFirstMismatch(beforeLines, afterLines)
            : AnalyzeWithLcs(beforeLines, afterLines, table);
    }

    private static List<MarkdownFrontmatterChange> AnalyzeWithLcs(
        string[] beforeLines,
        string[] afterLines,
        int[,] table)
    {
        var removed = new List<string>();
        var added = new List<string>();
        var changes = new List<MarkdownFrontmatterChange>();
        var beforeIndex = 0;
        var afterIndex = 0;

        while (beforeIndex < beforeLines.Length || afterIndex < afterLines.Length)
        {
            if (beforeIndex < beforeLines.Length
                && afterIndex < afterLines.Length
                && string.Equals(beforeLines[beforeIndex], afterLines[afterIndex], StringComparison.Ordinal))
            {
                FlushPending(changes, removed, added);
                beforeIndex++;
                afterIndex++;
            }
            else if (afterIndex < afterLines.Length
                && (beforeIndex == beforeLines.Length || table[beforeIndex, afterIndex + 1] >= table[beforeIndex + 1, afterIndex]))
            {
                added.Add(afterLines[afterIndex]);
                afterIndex++;
            }
            else if (beforeIndex < beforeLines.Length)
            {
                removed.Add(beforeLines[beforeIndex]);
                beforeIndex++;
            }
        }

        FlushPending(changes, removed, added);
        return changes;
    }

    private static List<MarkdownFrontmatterChange> AnalyzeFirstMismatch(
        string[] beforeLines,
        string[] afterLines)
    {
        var commonPrefix = 0;
        while (commonPrefix < beforeLines.Length
               && commonPrefix < afterLines.Length
               && string.Equals(
                   beforeLines[commonPrefix],
                   afterLines[commonPrefix],
                   StringComparison.Ordinal))
        {
            commonPrefix++;
        }

        var beforeEnd = beforeLines.Length;
        var afterEnd = afterLines.Length;
        while (beforeEnd > commonPrefix
               && afterEnd > commonPrefix
               && string.Equals(
                   beforeLines[beforeEnd - 1],
                   afterLines[afterEnd - 1],
                   StringComparison.Ordinal))
        {
            beforeEnd--;
            afterEnd--;
        }

        var hasBefore = commonPrefix < beforeEnd;
        var hasAfter = commonPrefix < afterEnd;
        if (!hasBefore && !hasAfter)
        {
            return [];
        }
        return
        [
            new MarkdownFrontmatterChange(
                hasBefore && hasAfter
                    ? DocsVersionChangeKind.Updated
                    : hasBefore
                        ? DocsVersionChangeKind.Removed
                        : DocsVersionChangeKind.Added,
                hasBefore ? beforeLines[commonPrefix] : null,
                hasAfter ? afterLines[commonPrefix] : null),
        ];
    }

    private static int[,]? BuildLcsTableIfBounded(
        string[] beforeLines,
        string[] afterLines)
    {
        if ((long)(beforeLines.Length + 1) * (afterLines.Length + 1) > MaxLcsCells)
        {
            return null;
        }

        var table = new int[beforeLines.Length + 1, afterLines.Length + 1];
        for (var beforeIndex = beforeLines.Length - 1; beforeIndex >= 0; beforeIndex--)
        {
            for (var afterIndex = afterLines.Length - 1; afterIndex >= 0; afterIndex--)
            {
                table[beforeIndex, afterIndex] = string.Equals(beforeLines[beforeIndex], afterLines[afterIndex], StringComparison.Ordinal)
                    ? table[beforeIndex + 1, afterIndex + 1] + 1
                    : Math.Max(table[beforeIndex + 1, afterIndex], table[beforeIndex, afterIndex + 1]);
            }
        }
        return table;
    }

    private static string[] SplitFrontmatterLines(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return Array.Empty<string>();
        }

        var frontmatter = ExtractFrontmatter(markdown);
        if (string.IsNullOrEmpty(frontmatter))
        {
            return Array.Empty<string>();
        }

        return frontmatter
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => line.TrimEnd())
            .Where(static line => line.Length > 0)
            .ToArray();
    }

    internal static string ExtractFrontmatter(string markdown)
        => TryExtractFrontmatter(markdown, out var frontmatter)
            ? frontmatter
            : string.Empty;

    internal static bool HasFrontmatter(string? markdown)
        => !string.IsNullOrEmpty(markdown)
            && TryExtractFrontmatter(markdown, out _);

    private static bool TryExtractFrontmatter(
        string markdown,
        out string frontmatter)
    {
        frontmatter = string.Empty;
        if (!markdown.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        var span = markdown.AsSpan();
        var openLineEnd = span.IndexOf('\n');
        if (openLineEnd < 0)
        {
            return false;
        }

        var openLine = span[..openLineEnd].TrimEnd('\r');
        if (!openLine.SequenceEqual("---"))
        {
            return false;
        }

        var rest = span[(openLineEnd + 1)..];
        var cursor = 0;
        while (cursor < rest.Length)
        {
            var remainder = rest[cursor..];
            var lineEnd = remainder.IndexOf('\n');
            var lineLength = lineEnd < 0 ? remainder.Length : lineEnd;
            var line = remainder[..lineLength].TrimEnd('\r');
            if (line.SequenceEqual("---"))
            {
                frontmatter = rest[..cursor].ToString();
                return true;
            }
            cursor += lineLength + (lineEnd < 0 ? 0 : 1);
            if (lineEnd < 0)
            {
                break;
            }
        }

        return false;
    }

    private static void FlushPending(
        List<MarkdownFrontmatterChange> changes,
        List<string> removed,
        List<string> added)
    {
        var pairCount = Math.Min(removed.Count, added.Count);
        for (var index = 0; index < pairCount; index++)
        {
            changes.Add(new MarkdownFrontmatterChange(
                DocsVersionChangeKind.Updated,
                removed[index],
                added[index]));
        }

        for (var index = pairCount; index < removed.Count; index++)
        {
            changes.Add(new MarkdownFrontmatterChange(
                DocsVersionChangeKind.Removed,
                removed[index],
                null));
        }

        for (var index = pairCount; index < added.Count; index++)
        {
            changes.Add(new MarkdownFrontmatterChange(
                DocsVersionChangeKind.Added,
                null,
                added[index]));
        }

        removed.Clear();
        added.Clear();
    }
}
