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
        if (beforeMarkdown is null && afterMarkdown == string.Empty)
        {
            return new MarkdownSourceChangeSummary(
                MarkdownSourceChangeKind.SourceOnly,
                null,
                string.Empty,
                1);
        }
        if (beforeMarkdown == string.Empty && afterMarkdown is null)
        {
            return new MarkdownSourceChangeSummary(
                MarkdownSourceChangeKind.SourceOnly,
                string.Empty,
                null,
                1);
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
        if (beforeMatches.Count == 0 && afterMatches.Count == 0)
        {
            return false;
        }

        if (!string.Equals(
                VariableReferenceRegex().Replace(beforeSource, string.Empty),
                VariableReferenceRegex().Replace(afterSource, string.Empty),
                StringComparison.Ordinal))
        {
            return false;
        }

        var beforeKeys = beforeMatches
            .Select(static match => match.Groups["key"].Value)
            .ToArray();
        var afterKeys = afterMatches
            .Select(static match => match.Groups["key"].Value)
            .ToArray();
        var commonPrefix = 0;
        while (commonPrefix < beforeKeys.Length
               && commonPrefix < afterKeys.Length
               && string.Equals(
                   beforeKeys[commonPrefix],
                   afterKeys[commonPrefix],
                   StringComparison.Ordinal))
        {
            commonPrefix++;
        }

        var beforeEnd = beforeKeys.Length;
        var afterEnd = afterKeys.Length;
        while (beforeEnd > commonPrefix
               && afterEnd > commonPrefix
               && string.Equals(
                   beforeKeys[beforeEnd - 1],
                   afterKeys[afterEnd - 1],
                   StringComparison.Ordinal))
        {
            beforeEnd--;
            afterEnd--;
        }

        var changeCount = Math.Max(beforeEnd - commonPrefix, afterEnd - commonPrefix);
        if (changeCount == 0)
        {
            return false;
        }
        summary = new MarkdownSourceChangeSummary(
            MarkdownSourceChangeKind.LiquidVariableReference,
            commonPrefix < beforeEnd ? beforeKeys[commonPrefix] : null,
            commonPrefix < afterEnd ? afterKeys[commonPrefix] : null,
            changeCount);
        return true;
    }

    private static (string? Before, string? After, int ChangeCount) FindFirstChangedLine(
        string? before,
        string? after)
    {
        var beforeLines = SplitLines(before);
        var afterLines = SplitLines(after);
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

        var anchor = FindNextCommonLine(
            beforeLines,
            commonPrefix,
            beforeEnd,
            afterLines,
            commonPrefix,
            afterEnd);
        var excerpts = AbbreviatePair(
            anchor.BeforeIndex > commonPrefix ? beforeLines[commonPrefix] : null,
            anchor.AfterIndex > commonPrefix ? afterLines[commonPrefix] : null);
        var changeCount = CalculateLineChangeCount(beforeLines, afterLines);
        return (excerpts.Before, excerpts.After, Math.Max(changeCount, 1));
    }

    private static int CalculateLineChangeCount(string[] beforeLines, string[] afterLines)
    {
        var remainingBeforeOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in beforeLines)
        {
            remainingBeforeOccurrences.TryGetValue(line, out var occurrenceCount);
            remainingBeforeOccurrences[line] = occurrenceCount + 1;
        }

        var unchangedLineCount = 0;
        foreach (var line in afterLines)
        {
            if (!remainingBeforeOccurrences.TryGetValue(line, out var occurrenceCount)
                || occurrenceCount == 0)
            {
                continue;
            }
            remainingBeforeOccurrences[line] = occurrenceCount - 1;
            unchangedLineCount++;
        }

        return Math.Max(
            beforeLines.Length - unchangedLineCount,
            afterLines.Length - unchangedLineCount);
    }

    private static (int BeforeIndex, int AfterIndex) FindNextCommonLine(
        string[] beforeLines,
        int beforeStart,
        int beforeEnd,
        string[] afterLines,
        int afterStart,
        int afterEnd)
    {
        var firstAfterIndexByLine = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var afterIndex = afterStart; afterIndex < afterEnd; afterIndex++)
        {
            firstAfterIndexByLine.TryAdd(afterLines[afterIndex], afterIndex);
        }

        var bestBeforeIndex = beforeEnd;
        var bestAfterIndex = afterEnd;
        var bestDistance = int.MaxValue;
        for (var beforeIndex = beforeStart; beforeIndex < beforeEnd; beforeIndex++)
        {
            var beforeDistance = beforeIndex - beforeStart;
            if (beforeDistance > bestDistance)
            {
                break;
            }
            if (!firstAfterIndexByLine.TryGetValue(beforeLines[beforeIndex], out var afterIndex))
            {
                continue;
            }

            var distance = beforeDistance + afterIndex - afterStart;
            if (distance < bestDistance)
            {
                bestBeforeIndex = beforeIndex;
                bestAfterIndex = afterIndex;
                bestDistance = distance;
            }
        }

        return (bestBeforeIndex, bestAfterIndex);
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
