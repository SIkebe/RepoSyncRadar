using System.Text.RegularExpressions;

namespace RepoSyncRadar.Core.Services.Preview;

public enum MarkdownSourceChangeKind
{
    LiquidVariableReference,
    Frontmatter,
    SourceOnly,
}

public sealed record MarkdownSourceChangeSummary(
    MarkdownSourceChangeKind Kind,
    string? Before,
    string? After,
    int ChangeCount);

public static partial class MarkdownSourceChangeAnalyzer
{
    private const int _maxExcerptLength = 160;

    [GeneratedRegex(
        @"(?:\{%-?\s*data\s+variables\.(?<key>[A-Za-z0-9_.\-/+_\[\]]+)\s*-?%\}|\{\{-?\s*(?:site\.data\.)?variables\.(?<key>[A-Za-z0-9_.\-/+_\[\]]+)\s*-?\}\})",
        RegexOptions.IgnoreCase)]
    private static partial Regex VariableReferenceRegex();

    public static MarkdownSourceChangeSummary? Analyze(
        string? beforeMarkdown,
        string? afterMarkdown,
        IReadOnlyList<MarkdownFrontmatterChange>? frontmatterChanges = null)
    {
        if (string.Equals(beforeMarkdown, afterMarkdown, StringComparison.Ordinal))
        {
            return null;
        }

        var beforeBody = DocsVersionImpactAnalyzer.StripFrontmatter(beforeMarkdown) ?? string.Empty;
        var afterBody = DocsVersionImpactAnalyzer.StripFrontmatter(afterMarkdown) ?? string.Empty;
        if (!string.Equals(beforeBody, afterBody, StringComparison.Ordinal))
        {
            if (TryAnalyzeLiquidVariableReferenceChanges(beforeBody, afterBody, out var liquidSummary))
            {
                return liquidSummary;
            }

            var bodyChange = FindFirstChangedLine(beforeBody, afterBody);
            if (bodyChange.Before is not null || bodyChange.After is not null)
            {
                return new MarkdownSourceChangeSummary(
                    MarkdownSourceChangeKind.SourceOnly,
                    bodyChange.Before,
                    bodyChange.After,
                    bodyChange.ChangeCount);
            }
        }

        var changes = frontmatterChanges ?? MarkdownFrontmatterDiffAnalyzer.Analyze(beforeMarkdown, afterMarkdown);
        if (changes.Count == 0)
        {
            return null;
        }

        var first = changes[0];
        var excerpts = AbbreviatePair(first.BeforeLine, first.AfterLine);
        return new MarkdownSourceChangeSummary(
            MarkdownSourceChangeKind.Frontmatter,
            excerpts.Before,
            excerpts.After,
            changes.Count);
    }

    private static bool TryAnalyzeLiquidVariableReferenceChanges(
        string? before,
        string? after,
        out MarkdownSourceChangeSummary? summary)
    {
        summary = null;
        var beforeSource = before ?? string.Empty;
        var afterSource = after ?? string.Empty;
        var beforeMatches = VariableReferenceRegex().Matches(beforeSource);
        var afterMatches = VariableReferenceRegex().Matches(afterSource);
        if (beforeMatches.Count == 0 || beforeMatches.Count != afterMatches.Count)
        {
            return false;
        }

        const string placeholder = "{rsr-variable}";
        if (!string.Equals(
                VariableReferenceRegex().Replace(beforeSource, placeholder),
                VariableReferenceRegex().Replace(afterSource, placeholder),
                StringComparison.Ordinal))
        {
            return false;
        }

        var changed = new List<(string Before, string After)>();
        for (var index = 0; index < beforeMatches.Count; index++)
        {
            var beforeKey = beforeMatches[index].Groups["key"].Value;
            var afterKey = afterMatches[index].Groups["key"].Value;
            if (!string.Equals(beforeKey, afterKey, StringComparison.Ordinal))
            {
                changed.Add((beforeKey, afterKey));
            }
        }

        if (changed.Count == 0)
        {
            return false;
        }

        summary = new MarkdownSourceChangeSummary(
            MarkdownSourceChangeKind.LiquidVariableReference,
            changed[0].Before,
            changed[0].After,
            changed.Count);
        return true;
    }

    private static (string? Before, string? After, int ChangeCount) FindFirstChangedLine(
        string? before,
        string? after)
    {
        var beforeLines = SplitLines(before);
        var afterLines = SplitLines(after);
        var lcs = BuildLineLcsTable(beforeLines, afterLines);
        var firstRemoved = new List<string>();
        var firstAdded = new List<string>();
        var currentRemoved = new List<string>();
        var currentAdded = new List<string>();
        var removedCount = 0;
        var addedCount = 0;
        var beforeIndex = 0;
        var afterIndex = 0;

        while (beforeIndex < beforeLines.Length || afterIndex < afterLines.Length)
        {
            if (beforeIndex < beforeLines.Length
                && afterIndex < afterLines.Length
                && string.Equals(beforeLines[beforeIndex], afterLines[afterIndex], StringComparison.Ordinal))
            {
                CaptureFirstHunk(firstRemoved, firstAdded, currentRemoved, currentAdded);
                beforeIndex++;
                afterIndex++;
            }
            else if (afterIndex < afterLines.Length
                && (beforeIndex == beforeLines.Length
                    || lcs[beforeIndex, afterIndex + 1] >= lcs[beforeIndex + 1, afterIndex]))
            {
                currentAdded.Add(afterLines[afterIndex]);
                addedCount++;
                afterIndex++;
            }
            else
            {
                currentRemoved.Add(beforeLines[beforeIndex]);
                removedCount++;
                beforeIndex++;
            }
        }

        CaptureFirstHunk(firstRemoved, firstAdded, currentRemoved, currentAdded);
        var excerpts = AbbreviatePair(
            firstRemoved.Count > 0 ? firstRemoved[0] : null,
            firstAdded.Count > 0 ? firstAdded[0] : null);
        return (excerpts.Before, excerpts.After, Math.Max(Math.Max(removedCount, addedCount), 1));
    }

    private static int[,] BuildLineLcsTable(string[] beforeLines, string[] afterLines)
    {
        var table = new int[beforeLines.Length + 1, afterLines.Length + 1];
        for (var beforeIndex = beforeLines.Length - 1; beforeIndex >= 0; beforeIndex--)
        {
            for (var afterIndex = afterLines.Length - 1; afterIndex >= 0; afterIndex--)
            {
                table[beforeIndex, afterIndex] = string.Equals(
                    beforeLines[beforeIndex],
                    afterLines[afterIndex],
                    StringComparison.Ordinal)
                    ? table[beforeIndex + 1, afterIndex + 1] + 1
                    : Math.Max(table[beforeIndex + 1, afterIndex], table[beforeIndex, afterIndex + 1]);
            }
        }
        return table;
    }

    private static void CaptureFirstHunk(
        List<string> firstRemoved,
        List<string> firstAdded,
        List<string> currentRemoved,
        List<string> currentAdded)
    {
        if (firstRemoved.Count == 0
            && firstAdded.Count == 0
            && (currentRemoved.Count > 0 || currentAdded.Count > 0))
        {
            firstRemoved.AddRange(currentRemoved);
            firstAdded.AddRange(currentAdded);
        }
        currentRemoved.Clear();
        currentAdded.Clear();
    }

    private static string[] SplitLines(string? source)
        => string.IsNullOrEmpty(source)
            ? []
            : source
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

    private static string? Abbreviate(string? value)
    {
        if (value is null || value.Length <= _maxExcerptLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, _maxExcerptLength - 3), "...");
    }

    private static (string? Before, string? After) AbbreviatePair(string? before, string? after)
    {
        if (before is null || after is null)
        {
            return (Abbreviate(before), Abbreviate(after));
        }

        var differenceIndex = 0;
        while (differenceIndex < before.Length
               && differenceIndex < after.Length
               && before[differenceIndex] == after[differenceIndex])
        {
            differenceIndex++;
        }

        return (
            AbbreviateAround(before, differenceIndex),
            AbbreviateAround(after, differenceIndex));
    }

    private static string AbbreviateAround(string value, int center)
    {
        if (value.Length <= _maxExcerptLength)
        {
            return value;
        }

        const int ellipsisLength = 3;
        const int leadingContextLength = 60;
        var start = Math.Max(0, center - leadingContextLength);
        var hasLeadingEllipsis = start > 0;
        var availableLength = _maxExcerptLength - (hasLeadingEllipsis ? ellipsisLength : 0);
        var hasTrailingEllipsis = value.Length - start > availableLength;
        if (hasTrailingEllipsis)
        {
            availableLength -= ellipsisLength;
        }

        return string.Concat(
            hasLeadingEllipsis ? "..." : string.Empty,
            value.AsSpan(start, Math.Min(availableLength, value.Length - start)),
            hasTrailingEllipsis ? "..." : string.Empty);
    }

}
