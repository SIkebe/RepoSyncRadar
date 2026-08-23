namespace RepoSyncRadar.Core.Services.Preview;

public sealed record MarkdownFrontmatterChange(
    DocsVersionChangeKind Kind,
    string? BeforeLine,
    string? AfterLine);

public static class MarkdownFrontmatterDiffAnalyzer
{
    private const int MaxLcsCells = 1_000_000;
    private readonly record struct LineAnchor(int BeforeIndex, int AfterIndex);

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
            ? AnalyzeLinearSpace(beforeLines, afterLines)
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

    private static List<MarkdownFrontmatterChange> AnalyzeLinearSpace(
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

        var changes = new List<MarkdownFrontmatterChange>();
        var beforeIndex = commonPrefix;
        var afterIndex = commonPrefix;
        foreach (var anchor in FindPatienceAnchors(
                     beforeLines,
                     commonPrefix,
                     beforeEnd,
                     afterLines,
                     commonPrefix,
                     afterEnd))
        {
            AppendChangeRange(
                changes,
                beforeLines,
                beforeIndex,
                anchor.BeforeIndex,
                afterLines,
                afterIndex,
                anchor.AfterIndex);
            beforeIndex = anchor.BeforeIndex + 1;
            afterIndex = anchor.AfterIndex + 1;
        }
        AppendChangeRange(
            changes,
            beforeLines,
            beforeIndex,
            beforeEnd,
            afterLines,
            afterIndex,
            afterEnd);
        return changes;
    }

    private static List<LineAnchor> FindPatienceAnchors(
        string[] beforeLines,
        int beforeStart,
        int beforeEnd,
        string[] afterLines,
        int afterStart,
        int afterEnd)
    {
        var beforeOccurrences = BuildLineOccurrences(beforeLines, beforeStart, beforeEnd);
        var afterOccurrences = BuildLineOccurrences(afterLines, afterStart, afterEnd);
        var candidates = new List<LineAnchor>();
        for (var index = beforeStart; index < beforeEnd; index++)
        {
            var line = beforeLines[index];
            if (beforeOccurrences[line].Count == 1
                && afterOccurrences.TryGetValue(line, out var afterOccurrence)
                && afterOccurrence.Count == 1)
            {
                candidates.Add(new LineAnchor(index, afterOccurrence.Index));
            }
        }
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var tails = new int[candidates.Count];
        var predecessors = new int[candidates.Count];
        Array.Fill(predecessors, -1);
        var length = 0;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var low = 0;
            var high = length;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (candidates[tails[middle]].AfterIndex < candidates[candidateIndex].AfterIndex)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            if (low > 0)
            {
                predecessors[candidateIndex] = tails[low - 1];
            }
            tails[low] = candidateIndex;
            if (low == length)
            {
                length++;
            }
        }

        var anchors = new List<LineAnchor>(length);
        var current = tails[length - 1];
        while (current >= 0)
        {
            anchors.Add(candidates[current]);
            current = predecessors[current];
        }
        anchors.Reverse();
        return anchors;
    }

    private static Dictionary<string, (int Count, int Index)> BuildLineOccurrences(
        string[] lines,
        int start,
        int end)
    {
        var occurrences = new Dictionary<string, (int Count, int Index)>(StringComparer.Ordinal);
        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            occurrences[line] = occurrences.TryGetValue(line, out var occurrence)
                ? (occurrence.Count + 1, occurrence.Index)
                : (1, index);
        }
        return occurrences;
    }

    private static void AppendChangeRange(
        List<MarkdownFrontmatterChange> changes,
        string[] beforeLines,
        int beforeStart,
        int beforeEnd,
        string[] afterLines,
        int afterStart,
        int afterEnd)
    {
        var pairCount = Math.Min(beforeEnd - beforeStart, afterEnd - afterStart);
        for (var index = 0; index < pairCount; index++)
        {
            changes.Add(new MarkdownFrontmatterChange(
                DocsVersionChangeKind.Updated,
                beforeLines[beforeStart + index],
                afterLines[afterStart + index]));
        }
        for (var index = beforeStart + pairCount; index < beforeEnd; index++)
        {
            changes.Add(new MarkdownFrontmatterChange(
                DocsVersionChangeKind.Removed,
                beforeLines[index],
                null));
        }
        for (var index = afterStart + pairCount; index < afterEnd; index++)
        {
            changes.Add(new MarkdownFrontmatterChange(
                DocsVersionChangeKind.Added,
                null,
                afterLines[index]));
        }
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
