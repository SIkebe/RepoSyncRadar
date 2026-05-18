namespace RepoSyncRadar.Core.Services.Preview;

public sealed record MarkdownFrontmatterChange(
    DocsVersionChangeKind Kind,
    string? BeforeLine,
    string? AfterLine);

public static class MarkdownFrontmatterDiffAnalyzer
{
    public static IReadOnlyList<MarkdownFrontmatterChange> Analyze(string? beforeMarkdown, string? afterMarkdown)
    {
        var beforeLines = SplitFrontmatterLines(beforeMarkdown);
        var afterLines = SplitFrontmatterLines(afterMarkdown);
        if (beforeLines.Length == 0 && afterLines.Length == 0)
        {
            return Array.Empty<MarkdownFrontmatterChange>();
        }

        var table = BuildLcsTable(beforeLines, afterLines);
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

    private static string ExtractFrontmatter(string markdown)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var span = markdown.AsSpan();
        var openLineEnd = span.IndexOf('\n');
        if (openLineEnd < 0)
        {
            return string.Empty;
        }

        var openLine = span[..openLineEnd].TrimEnd('\r');
        if (!openLine.SequenceEqual("---"))
        {
            return string.Empty;
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
                return rest[..cursor].ToString();
            }
            cursor += lineLength + (lineEnd < 0 ? 0 : 1);
            if (lineEnd < 0)
            {
                break;
            }
        }

        return string.Empty;
    }

    private static int[,] BuildLcsTable(string[] beforeLines, string[] afterLines)
    {
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
