using System.Text.RegularExpressions;

namespace RepoSyncRadar.Core.Services.Preview;

public sealed record MarkdownSourceDiffSummary(
    IReadOnlyList<MarkdownIfversionChange> IfversionChanges,
    IReadOnlyList<MarkdownRelatedSourceFileChange> RelatedFileChanges)
{
    public bool HasChanges => IfversionChanges.Count > 0 || RelatedFileChanges.Count > 0;
}

public sealed record MarkdownIfversionChange(
    DocsVersionChangeKind Kind,
    string? BeforeExpression,
    string? AfterExpression,
    string? BeforePreview = null,
    string? AfterPreview = null);

public sealed record MarkdownRelatedSourceFileChange(
    string Path,
    IReadOnlyList<MarkdownSourceLineChange> Changes);

public sealed record MarkdownSourceLineChange(
    DocsVersionChangeKind Kind,
    string? BeforeLine,
    string? AfterLine);

public static partial class MarkdownSourceDiffAnalyzer
{
    private const int MaxPreviewLines = 5;
    private const int MaxPreviewLength = 700;

    private static readonly HashSet<string> s_nonFeatureIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "and",
        "or",
        "not",
        "fpt",
        "ghec",
        "ghes",
        "ghae",
    };

    [GeneratedRegex(@"\{%-?\s*(?:ifversion|elsif)\s+(?<expr>.*?)\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex IfversionExpressionRegex();

    [GeneratedRegex(@"\{%-?\s*(?:ifversion|elsif|else|endif)\b.*?-?%\}", RegexOptions.Singleline)]
    private static partial Regex LiquidBoundaryRegex();

    [GeneratedRegex(@"\b[a-zA-Z_][a-zA-Z0-9_-]*\b")]
    private static partial Regex IdentifierRegex();

    public static MarkdownSourceDiffSummary Analyze(
        string? beforeMarkdown,
        string? afterMarkdown,
        string? beforeWorktreePath = null,
        string? afterWorktreePath = null)
    {
        var ifversionChanges = AnalyzeIfversionChanges(beforeMarkdown, afterMarkdown);
        var featureIds = ifversionChanges
            .SelectMany(static change => ExtractFeatureIdentifiers(change.BeforeExpression)
                .Concat(ExtractFeatureIdentifiers(change.AfterExpression)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var relatedChanges = AnalyzeRelatedFeatureFiles(featureIds, beforeWorktreePath, afterWorktreePath);
        return new MarkdownSourceDiffSummary(ifversionChanges, relatedChanges);
    }

    private static IReadOnlyList<MarkdownIfversionChange> AnalyzeIfversionChanges(string? beforeMarkdown, string? afterMarkdown)
    {
        var beforeBlocks = ExtractIfversionBlocks(beforeMarkdown);
        var afterBlocks = ExtractIfversionBlocks(afterMarkdown);
        if (beforeBlocks.Length == 0 && afterBlocks.Length == 0)
        {
            return Array.Empty<MarkdownIfversionChange>();
        }

        var beforeExpressions = beforeBlocks.Select(static block => block.Expression).ToArray();
        var afterExpressions = afterBlocks.Select(static block => block.Expression).ToArray();
        var table = BuildLcsTable(beforeExpressions, afterExpressions);
        var removed = new List<IfversionBlock>();
        var added = new List<IfversionBlock>();
        var changes = new List<MarkdownIfversionChange>();
        var beforeIndex = 0;
        var afterIndex = 0;

        while (beforeIndex < beforeExpressions.Length || afterIndex < afterExpressions.Length)
        {
            if (beforeIndex < beforeExpressions.Length
                && afterIndex < afterExpressions.Length
                && string.Equals(beforeExpressions[beforeIndex], afterExpressions[afterIndex], StringComparison.Ordinal))
            {
                FlushIfversionChanges(changes, removed, added);
                beforeIndex++;
                afterIndex++;
            }
            else if (afterIndex < afterExpressions.Length
                && (beforeIndex == beforeExpressions.Length || table[beforeIndex, afterIndex + 1] >= table[beforeIndex + 1, afterIndex]))
            {
                added.Add(afterBlocks[afterIndex]);
                afterIndex++;
            }
            else if (beforeIndex < beforeExpressions.Length)
            {
                removed.Add(beforeBlocks[beforeIndex]);
                beforeIndex++;
            }
        }

        FlushIfversionChanges(changes, removed, added);
        return changes;
    }

    private static IReadOnlyList<MarkdownRelatedSourceFileChange> AnalyzeRelatedFeatureFiles(
        string[] featureIds,
        string? beforeWorktreePath,
        string? afterWorktreePath)
    {
        if (featureIds.Length == 0 || string.IsNullOrWhiteSpace(beforeWorktreePath) || string.IsNullOrWhiteSpace(afterWorktreePath))
        {
            return Array.Empty<MarkdownRelatedSourceFileChange>();
        }

        var beforeRoot = beforeWorktreePath;
        var afterRoot = afterWorktreePath;
        var related = new List<MarkdownRelatedSourceFileChange>();
        foreach (var featureId in featureIds)
        {
            var relativePath = Path.Combine("data", "features", featureId + ".yml");
            var before = ReadLinesOrEmpty(Path.Combine(beforeRoot, relativePath));
            var after = ReadLinesOrEmpty(Path.Combine(afterRoot, relativePath));
            var changes = AnalyzeLineChanges(before, after);
            if (changes.Count > 0)
            {
                related.Add(new MarkdownRelatedSourceFileChange(
                    relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    changes));
            }
        }
        return related;
    }

    private static IReadOnlyList<MarkdownSourceLineChange> AnalyzeLineChanges(string[] beforeLines, string[] afterLines)
    {
        if (beforeLines.Length == 0 && afterLines.Length == 0)
        {
            return Array.Empty<MarkdownSourceLineChange>();
        }

        var table = BuildLcsTable(beforeLines, afterLines);
        var removed = new List<string>();
        var added = new List<string>();
        var changes = new List<MarkdownSourceLineChange>();
        var beforeIndex = 0;
        var afterIndex = 0;

        while (beforeIndex < beforeLines.Length || afterIndex < afterLines.Length)
        {
            if (beforeIndex < beforeLines.Length
                && afterIndex < afterLines.Length
                && string.Equals(beforeLines[beforeIndex], afterLines[afterIndex], StringComparison.Ordinal))
            {
                FlushLineChanges(changes, removed, added);
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

        FlushLineChanges(changes, removed, added);
        return changes;
    }

    private static IfversionBlock[] ExtractIfversionBlocks(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Array.Empty<IfversionBlock>();
        }

        return IfversionExpressionRegex()
            .Matches(markdown)
            .Select(match => new IfversionBlock(
                NormalizeExpression(match.Groups["expr"].Value),
                BuildContentPreview(markdown, match.Index + match.Length)))
            .Where(static block => block.Expression.Length > 0)
            .ToArray();
    }

    private static string BuildContentPreview(string markdown, int contentStart)
    {
        var boundary = LiquidBoundaryRegex().Match(markdown, contentStart);
        var contentEnd = boundary.Success ? boundary.Index : markdown.Length;
        if (contentEnd <= contentStart)
        {
            return string.Empty;
        }

        var lines = markdown[contentStart..contentEnd]
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .Take(MaxPreviewLines)
            .ToArray();
        var preview = string.Join('\n', lines);
        return preview.Length <= MaxPreviewLength
            ? preview
            : preview[..MaxPreviewLength] + "...";
    }

    private static IEnumerable<string> ExtractFeatureIdentifiers(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return Array.Empty<string>();
        }

        return IdentifierRegex()
            .Matches(expression)
            .Select(static match => match.Value)
            .Where(static value => !s_nonFeatureIdentifiers.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ReadLinesOrEmpty(string path)
        => File.Exists(path)
            ? File.ReadAllLines(path)
                .Select(static line => line.TrimEnd())
                .Where(static line => line.Length > 0)
                .ToArray()
            : Array.Empty<string>();

    private static int[,] BuildLcsTable(string[] before, string[] after)
    {
        var table = new int[before.Length + 1, after.Length + 1];
        for (var beforeIndex = before.Length - 1; beforeIndex >= 0; beforeIndex--)
        {
            for (var afterIndex = after.Length - 1; afterIndex >= 0; afterIndex--)
            {
                table[beforeIndex, afterIndex] = string.Equals(before[beforeIndex], after[afterIndex], StringComparison.Ordinal)
                    ? table[beforeIndex + 1, afterIndex + 1] + 1
                    : Math.Max(table[beforeIndex + 1, afterIndex], table[beforeIndex, afterIndex + 1]);
            }
        }
        return table;
    }

    private static void FlushIfversionChanges(
        List<MarkdownIfversionChange> changes,
        List<IfversionBlock> removed,
        List<IfversionBlock> added)
    {
        var pairCount = Math.Min(removed.Count, added.Count);
        for (var index = 0; index < pairCount; index++)
        {
            changes.Add(new MarkdownIfversionChange(
                DocsVersionChangeKind.Updated,
                removed[index].Expression,
                added[index].Expression,
                removed[index].Preview,
                added[index].Preview));
        }
        for (var index = pairCount; index < removed.Count; index++)
        {
            changes.Add(new MarkdownIfversionChange(
                DocsVersionChangeKind.Removed,
                removed[index].Expression,
                null,
                removed[index].Preview));
        }
        for (var index = pairCount; index < added.Count; index++)
        {
            changes.Add(new MarkdownIfversionChange(
                DocsVersionChangeKind.Added,
                null,
                added[index].Expression,
                AfterPreview: added[index].Preview));
        }
        removed.Clear();
        added.Clear();
    }

    private static void FlushLineChanges(
        List<MarkdownSourceLineChange> changes,
        List<string> removed,
        List<string> added)
    {
        var pairCount = Math.Min(removed.Count, added.Count);
        for (var index = 0; index < pairCount; index++)
        {
            changes.Add(new MarkdownSourceLineChange(DocsVersionChangeKind.Updated, removed[index], added[index]));
        }
        for (var index = pairCount; index < removed.Count; index++)
        {
            changes.Add(new MarkdownSourceLineChange(DocsVersionChangeKind.Removed, removed[index], null));
        }
        for (var index = pairCount; index < added.Count; index++)
        {
            changes.Add(new MarkdownSourceLineChange(DocsVersionChangeKind.Added, null, added[index]));
        }
        removed.Clear();
        added.Clear();
    }

    private static string NormalizeExpression(string expression)
        => string.Join(' ', expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record IfversionBlock(string Expression, string Preview);
}
