using System.Text;
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
public static class DocsVersionImpactAnalyzer
{
    private static readonly MarkdownPipeline _comparisonPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private enum HtmlWhiteSpaceMode
    {
        Collapse,
        Preserve,
        PreLine,
    }

    private sealed record HtmlElementContext(
        string TagName,
        HtmlWhiteSpaceMode WhiteSpaceMode,
        bool IsRawText);

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
        var normalized = new StringBuilder(html.Length);
        var elements = new List<HtmlElementContext>();
        var index = 0;
        var pendingCollapsibleSpace = false;
        var hasInlineContent = false;
        while (index < html.Length)
        {
            if (elements.Count > 0 && elements[^1].IsRawText)
            {
                var context = elements[^1];
                var closingTag = FindRawTextClosingTag(html, index, context.TagName);
                if (closingTag != index)
                {
                    var textEnd = closingTag < 0 ? html.Length : closingTag;
                    AppendHtmlText(
                        html.AsSpan(index, textEnd - index),
                        context.WhiteSpaceMode,
                        normalized,
                        ref pendingCollapsibleSpace,
                        ref hasInlineContent);
                    index = textEnd;
                    continue;
                }
            }

            if (html.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = commentEnd < 0 ? html.Length : commentEnd + 3;
                continue;
            }

            if (html[index] != '<')
            {
                var textEnd = html.IndexOf('<', index);
                if (textEnd < 0)
                {
                    textEnd = html.Length;
                }
                var mode = elements.Count == 0
                    ? HtmlWhiteSpaceMode.Collapse
                    : elements[^1].WhiteSpaceMode;
                AppendHtmlText(
                    html.AsSpan(index, textEnd - index),
                    mode,
                    normalized,
                    ref pendingCollapsibleSpace,
                    ref hasInlineContent);
                index = textEnd;
                continue;
            }

            var tagEnd = FindHtmlTagEnd(html, index);
            var tag = html.AsSpan(index, tagEnd - index);
            var tagName = GetHtmlTagName(tag, out var isClosing, out var isSelfClosing);
            var isBlock = IsBlockElement(tagName);
            if (isBlock)
            {
                pendingCollapsibleSpace = false;
                hasInlineContent = false;
            }
            else if (isSelfClosing && pendingCollapsibleSpace && hasInlineContent)
            {
                normalized.Append(' ');
                pendingCollapsibleSpace = false;
            }

            normalized.Append(NormalizeHtmlTagSyntax(tag));
            if (isClosing)
            {
                PopHtmlElement(elements, tagName);
            }
            else if (!isSelfClosing && !IsVoidElement(tagName))
            {
                var inheritedMode = elements.Count == 0
                    ? HtmlWhiteSpaceMode.Collapse
                    : elements[^1].WhiteSpaceMode;
                var whiteSpaceMode = ResolveWhiteSpaceMode(tagName, tag, inheritedMode);
                elements.Add(new HtmlElementContext(
                    tagName,
                    whiteSpaceMode,
                    IsRawTextElement(tagName)));
            }
            else if (!isBlock)
            {
                hasInlineContent = true;
            }

            index = tagEnd;
        }

        return normalized.ToString().Trim();
    }

    private static void AppendHtmlText(
        ReadOnlySpan<char> text,
        HtmlWhiteSpaceMode mode,
        StringBuilder normalized,
        ref bool pendingCollapsibleSpace,
        ref bool hasInlineContent)
    {
        if (mode == HtmlWhiteSpaceMode.Preserve)
        {
            if (pendingCollapsibleSpace && hasInlineContent)
            {
                normalized.Append(' ');
            }
            pendingCollapsibleSpace = false;
            normalized.Append(text);
            hasInlineContent |= text.Length > 0;
            return;
        }

        var previousWasCarriageReturn = false;
        foreach (var current in text)
        {
            if (mode == HtmlWhiteSpaceMode.PreLine && current is '\r' or '\n')
            {
                if (current != '\n' || !previousWasCarriageReturn)
                {
                    normalized.Append('\n');
                }
                pendingCollapsibleSpace = false;
                hasInlineContent = false;
                previousWasCarriageReturn = current == '\r';
            }
            else if (IsCollapsibleHtmlWhitespace(current))
            {
                pendingCollapsibleSpace = true;
                previousWasCarriageReturn = false;
            }
            else
            {
                if (pendingCollapsibleSpace && hasInlineContent)
                {
                    normalized.Append(' ');
                }
                pendingCollapsibleSpace = false;
                normalized.Append(current);
                hasInlineContent = true;
                previousWasCarriageReturn = false;
            }
        }
    }

    private static bool IsCollapsibleHtmlWhitespace(char value)
        => value is ' ' or '\t' or '\n' or '\f' or '\r';

    private static HtmlWhiteSpaceMode ResolveWhiteSpaceMode(
        string tagName,
        ReadOnlySpan<char> tag,
        HtmlWhiteSpaceMode inheritedMode)
    {
        var mode = tagName is "pre" or "code" or "textarea" or "script" or "style"
            ? HtmlWhiteSpaceMode.Preserve
            : inheritedMode;
        var style = GetHtmlAttributeValue(tag, "style");
        if (style is null)
        {
            return mode;
        }

        foreach (var declaration in style.Split(';'))
        {
            var separator = declaration.IndexOf(':');
            if (separator < 0
                || !declaration.AsSpan(0, separator).Trim().Equals(
                    "white-space",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = declaration.AsSpan(separator + 1).Trim();
            if (value.StartsWith("normal", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("nowrap", StringComparison.OrdinalIgnoreCase))
            {
                mode = HtmlWhiteSpaceMode.Collapse;
            }
            else if (value.StartsWith("pre-line", StringComparison.OrdinalIgnoreCase))
            {
                mode = HtmlWhiteSpaceMode.PreLine;
            }
            else
            {
                mode = HtmlWhiteSpaceMode.Preserve;
            }
        }
        return mode;
    }

    private static string? GetHtmlAttributeValue(ReadOnlySpan<char> tag, string attributeName)
    {
        var index = 1;
        while (index < tag.Length && tag[index] is not ' ' and not '\t' and not '\n' and not '\f' and not '\r' and not '>')
        {
            index++;
        }

        while (index < tag.Length)
        {
            while (index < tag.Length && IsCollapsibleHtmlWhitespace(tag[index]))
            {
                index++;
            }
            if (index >= tag.Length || tag[index] is '>' or '/')
            {
                return null;
            }

            var nameStart = index;
            while (index < tag.Length
                   && !IsCollapsibleHtmlWhitespace(tag[index])
                   && tag[index] is not '=' and not '>' and not '/')
            {
                index++;
            }
            var name = tag[nameStart..index];
            while (index < tag.Length && IsCollapsibleHtmlWhitespace(tag[index]))
            {
                index++;
            }
            if (index >= tag.Length || tag[index] != '=')
            {
                continue;
            }

            index++;
            while (index < tag.Length && IsCollapsibleHtmlWhitespace(tag[index]))
            {
                index++;
            }
            if (index >= tag.Length)
            {
                return null;
            }

            var quote = tag[index] is '\'' or '"' ? tag[index++] : '\0';
            var valueStart = index;
            if (quote == '\0')
            {
                while (index < tag.Length
                       && !IsCollapsibleHtmlWhitespace(tag[index])
                       && tag[index] is not '>' and not '/')
                {
                    index++;
                }
            }
            else
            {
                while (index < tag.Length && tag[index] != quote)
                {
                    index++;
                }
            }

            if (name.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
            {
                return tag[valueStart..index].ToString();
            }
            if (quote != '\0' && index < tag.Length)
            {
                index++;
            }
        }

        return null;
    }

    private static string GetHtmlTagName(
        ReadOnlySpan<char> tag,
        out bool isClosing,
        out bool isSelfClosing)
    {
        var index = 1;
        isClosing = index < tag.Length && tag[index] == '/';
        if (isClosing)
        {
            index++;
        }
        while (index < tag.Length && IsCollapsibleHtmlWhitespace(tag[index]))
        {
            index++;
        }

        var nameStart = index;
        while (index < tag.Length
               && (char.IsAsciiLetterOrDigit(tag[index]) || tag[index] is '-' or ':'))
        {
            index++;
        }

        var end = tag.Length - 2;
        while (end >= 0 && IsCollapsibleHtmlWhitespace(tag[end]))
        {
            end--;
        }
        isSelfClosing = end >= 0 && tag[end] == '/';
        return tag[nameStart..index].ToString().ToLowerInvariant();
    }

    private static void PopHtmlElement(List<HtmlElementContext> elements, string tagName)
    {
        for (var index = elements.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(elements[index].TagName, tagName, StringComparison.Ordinal))
            {
                continue;
            }

            elements.RemoveRange(index, elements.Count - index);
            return;
        }
    }

    private static int FindRawTextClosingTag(string html, int startIndex, string tagName)
    {
        var search = $"</{tagName}";
        var index = startIndex;
        while ((index = html.IndexOf(search, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var afterName = index + search.Length;
            if (afterName >= html.Length
                || html[afterName] == '>'
                || IsCollapsibleHtmlWhitespace(html[afterName]))
            {
                return index;
            }
            index = afterName;
        }
        return -1;
    }

    private static bool IsRawTextElement(string tagName)
        => tagName is "textarea" or "script" or "style";

    private static bool IsVoidElement(string tagName)
        => tagName is "area" or "base" or "br" or "col" or "embed" or "hr" or "img"
            or "input" or "link" or "meta" or "param" or "source" or "track" or "wbr";

    private static bool IsBlockElement(string tagName)
        => tagName is "address" or "article" or "aside" or "blockquote" or "dd" or "details"
            or "dialog" or "div" or "dl" or "dt" or "fieldset" or "figcaption" or "figure"
            or "footer" or "form" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            or "header" or "hgroup" or "hr" or "li" or "main" or "nav" or "ol" or "p"
            or "pre" or "section" or "table" or "tbody" or "td" or "tfoot" or "th"
            or "thead" or "tr" or "ul";

    private static string NormalizeHtmlTagSyntax(ReadOnlySpan<char> tag)
    {
        var normalized = new StringBuilder(tag.Length);
        var quote = '\0';
        var pendingWhitespace = false;
        for (var index = 0; index < tag.Length; index++)
        {
            var current = tag[index];
            if (quote != '\0')
            {
                normalized.Append(current);
                if (current == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                normalized.Append(current);
                pendingWhitespace = false;
                continue;
            }

            if (current is ' ' or '\t' or '\n' or '\f' or '\r')
            {
                pendingWhitespace = true;
                continue;
            }

            if (pendingWhitespace
                && normalized.Length > 0
                && normalized[^1] is not '<' and not '='
                && current is not '>' and not '='
                && !(current == '/' && index + 1 < tag.Length && tag[index + 1] == '>'))
            {
                normalized.Append(' ');
            }
            normalized.Append(current);
            pendingWhitespace = false;
        }

        return normalized.ToString();
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
