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
            ? AnalyzeLinear(beforeLines, afterLines)
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

    private static List<MarkdownFrontmatterChange> AnalyzeLinear(
        string[] beforeLines,
        string[] afterLines)
    {
        var removed = new List<string>();
        var added = new List<string>();
        var changes = new List<MarkdownFrontmatterChange>();
        var afterIndexesByLine = BuildLineIndexQueues(afterLines);
        var beforeIndex = 0;
        var afterIndex = 0;

        while (beforeIndex < beforeLines.Length)
        {
            var beforeLine = beforeLines[beforeIndex];
            if (!afterIndexesByLine.TryGetValue(beforeLine, out var matchingAfterIndexes))
            {
                removed.Add(beforeLine);
                beforeIndex++;
                continue;
            }
            while (matchingAfterIndexes.Count > 0 && matchingAfterIndexes.Peek() < afterIndex)
            {
                matchingAfterIndexes.Dequeue();
            }
            if (matchingAfterIndexes.Count == 0)
            {
                removed.Add(beforeLine);
                beforeIndex++;
                continue;
            }

            var matchingAfterIndex = matchingAfterIndexes.Dequeue();
            while (afterIndex < matchingAfterIndex)
            {
                added.Add(afterLines[afterIndex]);
                afterIndex++;
            }
            FlushPending(changes, removed, added);
            beforeIndex++;
            afterIndex++;
        }
        while (afterIndex < afterLines.Length)
        {
            added.Add(afterLines[afterIndex]);
            afterIndex++;
        }

        FlushPending(changes, removed, added);
        return changes;
    }

    private static Dictionary<string, Queue<int>> BuildLineIndexQueues(string[] lines)
    {
        var indexesByLine = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            if (!indexesByLine.TryGetValue(lines[index], out var indexes))
            {
                indexes = new Queue<int>();
                indexesByLine.Add(lines[index], indexes);
            }
            indexes.Enqueue(index);
        }
        return indexesByLine;
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
