using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace RepoSyncRadar.Core.Services.Preview;

public enum DocsVersionChangeKind
{
    Added,
    Removed,
    Updated,
}

public sealed record DocsVersionChangeSnippet(
    DocsVersionChangeKind Kind,
    string? BeforeExcerpt,
    string? AfterExcerpt);

public sealed record DocsVersionImpactDetail(
    DocsVersion Version,
    IReadOnlyList<DocsVersionChangeSnippet> Changes);

/// <summary>
/// PR の before / after Markdown を <see cref="DocsVersionCatalog.All"/> の全版で
/// 評価し、レンダリング結果が異なる版だけを返す解析器
/// (IMPLEMENTATION_PLAN.md §Step 19.9)。<b>差分の見落とし防止が主目的</b>。
/// <para>
/// たとえば <c>{% ifversion ghec %}new ghec feature{% endif %}</c> が追加されただけの
/// PR は fpt/ghes 版では何も変わらず ghec 版だけが変わる。一覧から ghec が漏れていれば
/// レビュアーは「ghec の差分を見落とした」ことに即気づける。
/// </para>
/// <para>
/// 戦略: 全版に対して <see cref="DocsLiquidEvaluator.Evaluate(string?, DocsLiquidContext, DocsVersion, int)"/>
/// で展開し、出力文字列の Ordinal 比較で「影響あり」を判定する。
/// 性能は版数 (現状 8) × Liquid 評価コスト程度で十分小さい。
/// </para>
/// </summary>
public static partial class DocsVersionImpactAnalyzer
{
    private static readonly MarkdownPipeline _comparisonPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    [GeneratedRegex(
        @"<(?<tag>textarea|script|style)\b(?:[^>""']|""[^""]*""|'[^']*')*>.*?</\k<tag>\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RawTextElementRegex();

    [GeneratedRegex(
        @"<(?<tag>pre|code)\b(?:[^>""']|""[^""]*""|'[^']*')*>.*?</\k<tag>\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex WhitespaceSensitiveElementRegex();

    [GeneratedRegex(@"[ \t\n\f\r]+")]
    private static partial Regex CollapsibleWhitespaceRegex();

    /// <summary>
    /// 全版で評価し、before/after が一致しない版だけを <see cref="DocsVersionCatalog.All"/>
    /// の順序で返す。差分がない場合は空配列。
    /// </summary>
    public static IReadOnlyList<DocsVersion> Analyze(
        string? beforeMarkdown,
        DocsLiquidContext beforeContext,
        string? afterMarkdown,
        DocsLiquidContext afterContext)
        => AnalyzeDetails(beforeMarkdown, beforeContext, afterMarkdown, afterContext)
            .Select(static detail => detail.Version)
            .ToArray();

    /// <summary>
    /// 全版で評価し、before/after の本文差分の抜粋を版ごとに返す。
    /// </summary>
    public static IReadOnlyList<DocsVersionImpactDetail> AnalyzeDetails(
        string? beforeMarkdown,
        DocsLiquidContext beforeContext,
        string? afterMarkdown,
        DocsLiquidContext afterContext,
        string? beforeRepoPath = null,
        string? afterRepoPath = null)
    {
        ArgumentNullException.ThrowIfNull(beforeContext);
        ArgumentNullException.ThrowIfNull(afterContext);

        // 完全一致なら全版で影響なし — 早期 return。
        if (string.Equals(beforeMarkdown, afterMarkdown, StringComparison.Ordinal)
            && ReferenceEquals(beforeContext, afterContext)
            && string.Equals(beforeRepoPath, afterRepoPath, StringComparison.Ordinal))
        {
            return Array.Empty<DocsVersionImpactDetail>();
        }

        var beforeRenderable = StripFrontmatter(beforeMarkdown);
        var afterRenderable = StripFrontmatter(afterMarkdown);
        var affected = new List<DocsVersionImpactDetail>(DocsVersionCatalog.All.Count);
        foreach (var version in DocsVersionCatalog.All)
        {
            var beforeRendered = DocsLiquidEvaluator.Evaluate(beforeRenderable, beforeContext, version);
            var afterRendered = DocsLiquidEvaluator.Evaluate(afterRenderable, afterContext, version);
            if (!string.IsNullOrWhiteSpace(beforeRepoPath)
                && !string.IsNullOrWhiteSpace(afterRepoPath))
            {
                beforeRendered = MarkdownPreviewRenderer.RewriteAutotitleMarkdownLinks(
                    beforeRendered,
                    beforeRepoPath,
                    beforeContext,
                    version);
                afterRendered = MarkdownPreviewRenderer.RewriteAutotitleMarkdownLinks(
                    afterRendered,
                    afterRepoPath,
                    afterContext,
                    version);
            }
            if (!string.Equals(NormalizeForComparison(beforeRendered), NormalizeForComparison(afterRendered), StringComparison.Ordinal))
            {
                var changes = BuildChangeSnippets(beforeRendered, afterRendered);
                if (changes.Count == 0)
                {
                    changes.Add(new DocsVersionChangeSnippet(
                        DocsVersionChangeKind.Updated,
                        TrimExcerpt(Markdown.ToHtml(beforeRendered ?? string.Empty, _comparisonPipeline)),
                        TrimExcerpt(Markdown.ToHtml(afterRendered ?? string.Empty, _comparisonPipeline))));
                }
                affected.Add(new DocsVersionImpactDetail(version, changes));
            }
        }
        return affected;
    }

    internal static string? StripFrontmatter(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.StartsWith("---", StringComparison.Ordinal))
        {
            return markdown;
        }

        var span = markdown.AsSpan();
        var openLineEnd = span.IndexOf('\n');
        if (openLineEnd < 0)
        {
            return markdown;
        }

        var openLine = span[..openLineEnd].TrimEnd('\r');
        if (!openLine.SequenceEqual("---"))
        {
            return markdown;
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
                var bodyStart = cursor + lineLength + (lineEnd < 0 ? 0 : 1);
                return bodyStart >= rest.Length ? string.Empty : rest[bodyStart..].ToString();
            }

            cursor += lineLength + (lineEnd < 0 ? 0 : 1);
            if (lineEnd < 0)
            {
                break;
            }
        }

        return markdown;
    }

    /// <summary>
    /// <see cref="Analyze"/> の判定結果が「全版に影響」を意味するかを返す。
    /// PR ヘッダのバッジ表示で「すべての版」と要約するために使う。
    /// </summary>
    public static bool IsAllVersionsAffected(IReadOnlyList<DocsVersion> affected)
    {
        ArgumentNullException.ThrowIfNull(affected);
        return affected.Count == DocsVersionCatalog.All.Count;
    }

    private static List<DocsVersionChangeSnippet> BuildChangeSnippets(string? beforeRendered, string? afterRendered)
    {
        var beforeBlocks = SplitBlocks(beforeRendered);
        var afterBlocks = SplitBlocks(afterRendered);
        var snippets = new List<DocsVersionChangeSnippet>();
        var removed = new List<string>();
        var added = new List<string>();
        var table = BuildLcsTable(beforeBlocks, afterBlocks);
        var beforeIndex = 0;
        var afterIndex = 0;

        while (beforeIndex < beforeBlocks.Count || afterIndex < afterBlocks.Count)
        {
            if (beforeIndex < beforeBlocks.Count
                && afterIndex < afterBlocks.Count
                && string.Equals(beforeBlocks[beforeIndex], afterBlocks[afterIndex], StringComparison.Ordinal))
            {
                FlushPending(snippets, removed, added);
                beforeIndex++;
                afterIndex++;
            }
            else if (afterIndex < afterBlocks.Count
                && (beforeIndex == beforeBlocks.Count || table[beforeIndex, afterIndex + 1] >= table[beforeIndex + 1, afterIndex]))
            {
                added.Add(afterBlocks[afterIndex]);
                afterIndex++;
            }
            else if (beforeIndex < beforeBlocks.Count)
            {
                removed.Add(beforeBlocks[beforeIndex]);
                beforeIndex++;
            }
        }

        FlushPending(snippets, removed, added);
        return snippets;
    }

    private static int[,] BuildLcsTable(IReadOnlyList<string> beforeBlocks, IReadOnlyList<string> afterBlocks)
    {
        var table = new int[beforeBlocks.Count + 1, afterBlocks.Count + 1];
        for (var beforeIndex = beforeBlocks.Count - 1; beforeIndex >= 0; beforeIndex--)
        {
            for (var afterIndex = afterBlocks.Count - 1; afterIndex >= 0; afterIndex--)
            {
                table[beforeIndex, afterIndex] = string.Equals(beforeBlocks[beforeIndex], afterBlocks[afterIndex], StringComparison.Ordinal)
                    ? table[beforeIndex + 1, afterIndex + 1] + 1
                    : Math.Max(table[beforeIndex + 1, afterIndex], table[beforeIndex, afterIndex + 1]);
            }
        }
        return table;
    }

    private static void FlushPending(
        List<DocsVersionChangeSnippet> snippets,
        List<string> removed,
        List<string> added)
    {
        var pairCount = Math.Min(removed.Count, added.Count);
        for (var index = 0; index < pairCount; index++)
        {
            snippets.Add(new DocsVersionChangeSnippet(
                DocsVersionChangeKind.Updated,
                TrimExcerpt(removed[index]),
                TrimExcerpt(added[index])));
        }

        for (var index = pairCount; index < removed.Count; index++)
        {
            snippets.Add(new DocsVersionChangeSnippet(
                DocsVersionChangeKind.Removed,
                TrimExcerpt(removed[index]),
                null));
        }

        for (var index = pairCount; index < added.Count; index++)
        {
            snippets.Add(new DocsVersionChangeSnippet(
                DocsVersionChangeKind.Added,
                null,
                TrimExcerpt(added[index])));
        }

        removed.Clear();
        added.Clear();
    }

    private static IReadOnlyList<string> SplitBlocks(string? rendered)
    {
        if (string.IsNullOrWhiteSpace(rendered))
        {
            return Array.Empty<string>();
        }

        var normalized = rendered.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = new List<string>();
        var current = new List<string>();
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                AddCurrentBlock(blocks, current);
                continue;
            }

            // ATX 見出し (## ...) は常にブロック境界として扱う。Markdown では見出し
            // 直後の空行有無はレンダリング結果に影響しないため、空行が削除/追加された
            // だけの整形差で見出しと次の段落が結合され、版差分として誤検知されるのを防ぐ。
            if (IsAtxHeadingLine(line))
            {
                AddCurrentBlock(blocks, current);
                blocks.Add(line);
                continue;
            }

            current.Add(line);
        }
        AddCurrentBlock(blocks, current);
        return blocks;
    }

    private static bool IsAtxHeadingLine(string trimmedLine)
    {
        var hashCount = 0;
        while (hashCount < trimmedLine.Length && trimmedLine[hashCount] == '#')
        {
            hashCount++;
        }
        return hashCount is >= 1 and <= 6
            && hashCount < trimmedLine.Length
            && trimmedLine[hashCount] == ' ';
    }

    private static string NormalizeForComparison(string? rendered)
    {
        var html = Markdown.ToHtml(rendered ?? string.Empty, _comparisonPipeline);
        var sensitiveElements = new List<string>();
        var protectedHtml = ProtectElements(html, RawTextElementRegex(), sensitiveElements);
        var withoutComments = StripActualHtmlComments(protectedHtml);
        protectedHtml = ProtectElements(withoutComments, WhitespaceSensitiveElementRegex(), sensitiveElements);
        var normalized = NormalizeCollapsibleTextWhitespace(protectedHtml);
        for (var index = 0; index < sensitiveElements.Count; index++)
        {
            normalized = normalized.Replace(
                $"\uE000RSR{index}\uE001",
                sensitiveElements[index],
                StringComparison.Ordinal);
        }
        return normalized;
    }

    private static string ProtectElements(string html, Regex elementRegex, List<string> elements)
        => elementRegex.Replace(
            html,
            match =>
            {
                var index = elements.Count;
                elements.Add(match.Value);
                return $"\uE000RSR{index}\uE001";
            });

    private static string StripActualHtmlComments(string html)
    {
        var stripped = new StringBuilder(html.Length);
        var index = 0;
        while (index < html.Length)
        {
            if (html.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = commentEnd < 0 ? html.Length : commentEnd + 3;
                continue;
            }

            if (html[index] == '<')
            {
                var tagEnd = FindHtmlTagEnd(html, index);
                stripped.Append(html, index, tagEnd - index);
                index = tagEnd;
                continue;
            }

            stripped.Append(html[index]);
            index++;
        }
        return stripped.ToString();
    }

    private static string NormalizeCollapsibleTextWhitespace(string html)
    {
        var normalized = new StringBuilder(html.Length);
        var index = 0;
        while (index < html.Length)
        {
            if (html[index] != '<')
            {
                var textEnd = html.IndexOf('<', index);
                if (textEnd < 0)
                {
                    textEnd = html.Length;
                }
                normalized.Append(CollapsibleWhitespaceRegex().Replace(html[index..textEnd], " "));
                index = textEnd;
                continue;
            }

            var tagEnd = FindHtmlTagEnd(html, index);
            normalized.Append(html, index, tagEnd - index);
            index = tagEnd;
        }

        return normalized.ToString().Trim();
    }

    private static int FindHtmlTagEnd(string html, int startIndex)
    {
        var quote = '\0';
        for (var index = startIndex; index < html.Length; index++)
        {
            var current = html[index];
            if (quote == '\0' && current is '\'' or '"')
            {
                quote = current;
            }
            else if (current == quote)
            {
                quote = '\0';
            }
            else if (current == '>' && quote == '\0')
            {
                return index + 1;
            }
        }

        return html.Length;
    }

    private static void AddCurrentBlock(List<string> blocks, List<string> current)
    {
        if (current.Count == 0)
        {
            return;
        }
        blocks.Add(string.Join(' ', current));
        current.Clear();
    }

    private static string TrimExcerpt(string value)
    {
        const int maxLength = 220;
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "...";
    }
}
