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

    [GeneratedRegex(@"\{%-?\s*data\s+variables\.(?<key>[A-Za-z0-9_.\-/+_\[\]]+)\s*-?%\}", RegexOptions.IgnoreCase)]
    private static partial Regex DataVariableReferenceRegex();

    public static MarkdownSourceChangeSummary? Analyze(
        string? beforeMarkdown,
        string? afterMarkdown,
        IReadOnlyList<MarkdownFrontmatterChange>? frontmatterChanges = null)
    {
        if (string.Equals(beforeMarkdown, afterMarkdown, StringComparison.Ordinal))
        {
            return null;
        }

        var beforeBody = StripFrontmatter(beforeMarkdown);
        var afterBody = StripFrontmatter(afterMarkdown);
        if (!string.Equals(beforeBody, afterBody, StringComparison.Ordinal))
        {
            if (TryAnalyzeLiquidVariableReferenceChanges(beforeBody, afterBody, out var liquidSummary))
            {
                return liquidSummary;
            }

            var bodyChange = FindFirstChangedLine(beforeBody, afterBody);
            return new MarkdownSourceChangeSummary(
                MarkdownSourceChangeKind.SourceOnly,
                bodyChange.Before,
                bodyChange.After,
                bodyChange.ChangeCount);
        }

        var changes = frontmatterChanges ?? MarkdownFrontmatterDiffAnalyzer.Analyze(beforeMarkdown, afterMarkdown);
        if (changes.Count == 0)
        {
            return null;
        }

        var first = changes[0];
        return new MarkdownSourceChangeSummary(
            MarkdownSourceChangeKind.Frontmatter,
            Abbreviate(first.BeforeLine),
            Abbreviate(first.AfterLine),
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
        var beforeMatches = DataVariableReferenceRegex().Matches(beforeSource);
        var afterMatches = DataVariableReferenceRegex().Matches(afterSource);
        if (beforeMatches.Count == 0 || beforeMatches.Count != afterMatches.Count)
        {
            return false;
        }

        const string placeholder = "{% data variables.__rsr__ %}";
        if (!string.Equals(
                DataVariableReferenceRegex().Replace(beforeSource, placeholder),
                DataVariableReferenceRegex().Replace(afterSource, placeholder),
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
        var commonPrefix = 0;
        while (commonPrefix < beforeLines.Length
               && commonPrefix < afterLines.Length
               && string.Equals(beforeLines[commonPrefix], afterLines[commonPrefix], StringComparison.Ordinal))
        {
            commonPrefix++;
        }

        var beforeEnd = beforeLines.Length - 1;
        var afterEnd = afterLines.Length - 1;
        while (beforeEnd >= commonPrefix
               && afterEnd >= commonPrefix
               && string.Equals(beforeLines[beforeEnd], afterLines[afterEnd], StringComparison.Ordinal))
        {
            beforeEnd--;
            afterEnd--;
        }

        var beforeExcerpt = commonPrefix <= beforeEnd ? Abbreviate(beforeLines[commonPrefix]) : null;
        var afterExcerpt = commonPrefix <= afterEnd ? Abbreviate(afterLines[commonPrefix]) : null;
        var changeCount = Math.Max(beforeEnd - commonPrefix + 1, afterEnd - commonPrefix + 1);
        return (beforeExcerpt, afterExcerpt, Math.Max(changeCount, 1));
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

    private static string? StripFrontmatter(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.StartsWith("---", StringComparison.Ordinal))
        {
            return markdown;
        }

        var normalized = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var closingDelimiter = normalized.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        return closingDelimiter < 0
            ? markdown
            : normalized[(closingDelimiter + 5)..];
    }
}
