using System.Net;
using System.Text;
using Markdig;
using Markdig.Helpers;

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
        bool IsRawText,
        bool IsForeignContent,
        bool ExposesWhitespaceBoundary);

    /// <summary>
    /// 全版で評価し、before/after が一致しない版だけを <see cref="DocsVersionCatalog.All"/>
    /// の順序で返す。差分がない場合は空配列。
    /// </summary>
    public static IReadOnlyList<DocsVersion> Analyze(
        string? beforeMarkdown,
        DocsLiquidContext beforeContext,
        string? afterMarkdown,
        DocsLiquidContext afterContext,
        string? beforeRepoPath = null,
        string? afterRepoPath = null)
        => AnalyzeCore(
            beforeMarkdown,
            beforeContext,
            afterMarkdown,
            afterContext,
            beforeRepoPath,
            afterRepoPath,
            CancellationToken.None);

    internal static IReadOnlyList<DocsVersion> AnalyzeCancellable(
        string? beforeMarkdown,
        DocsLiquidContext beforeContext,
        string? afterMarkdown,
        DocsLiquidContext afterContext,
        string? beforeRepoPath,
        string? afterRepoPath,
        CancellationToken cancellationToken)
        => AnalyzeCore(
            beforeMarkdown,
            beforeContext,
            afterMarkdown,
            afterContext,
            beforeRepoPath,
            afterRepoPath,
            cancellationToken);

    private static IReadOnlyList<DocsVersion> AnalyzeCore(
        string? beforeMarkdown,
        DocsLiquidContext beforeContext,
        string? afterMarkdown,
        DocsLiquidContext afterContext,
        string? beforeRepoPath = null,
        string? afterRepoPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforeContext);
        ArgumentNullException.ThrowIfNull(afterContext);

        if (string.Equals(beforeMarkdown, afterMarkdown, StringComparison.Ordinal)
            && ReferenceEquals(beforeContext, afterContext)
            && string.Equals(beforeRepoPath, afterRepoPath, StringComparison.Ordinal))
        {
            return Array.Empty<DocsVersion>();
        }

        var beforeRenderable = StripFrontmatter(beforeMarkdown);
        var afterRenderable = StripFrontmatter(afterMarkdown);
        var affected = new List<DocsVersion>(DocsVersionCatalog.All.Count);
        foreach (var version in DocsVersionCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            if (string.Equals(beforeRendered, afterRendered, StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.Equals(
                    NormalizeForComparison(beforeRendered, cancellationToken),
                    NormalizeForComparison(afterRendered, cancellationToken),
                    StringComparison.Ordinal))
            {
                affected.Add(version);
            }
        }
        return affected;
    }

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
        => AnalyzeDetailsCore(
            beforeMarkdown,
            beforeContext,
            afterMarkdown,
            afterContext,
            beforeRepoPath,
            afterRepoPath,
            CancellationToken.None);

    internal static IReadOnlyList<DocsVersionImpactDetail> AnalyzeDetailsCancellable(
        string? beforeMarkdown,
        DocsLiquidContext beforeContext,
        string? afterMarkdown,
        DocsLiquidContext afterContext,
        string? beforeRepoPath,
        string? afterRepoPath,
        CancellationToken cancellationToken)
        => AnalyzeDetailsCore(
            beforeMarkdown,
            beforeContext,
            afterMarkdown,
            afterContext,
            beforeRepoPath,
            afterRepoPath,
            cancellationToken);

    private static IReadOnlyList<DocsVersionImpactDetail> AnalyzeDetailsCore(
        string? beforeMarkdown,
        DocsLiquidContext beforeContext,
        string? afterMarkdown,
        DocsLiquidContext afterContext,
        string? beforeRepoPath = null,
        string? afterRepoPath = null,
        CancellationToken cancellationToken = default)
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
            cancellationToken.ThrowIfCancellationRequested();
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
            if (string.Equals(beforeRendered, afterRendered, StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.Equals(
                    NormalizeForComparison(beforeRendered, cancellationToken),
                    NormalizeForComparison(afterRendered, cancellationToken),
                    StringComparison.Ordinal))
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

    private static string NormalizeForComparison(
        string? rendered,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preprocessed = MarkdownPreviewRenderer.PreprocessMarkdownForComparison(
            rendered ?? string.Empty,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var html = Markdown.ToHtml(preprocessed, _comparisonPipeline);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = new StringBuilder(html.Length);
        var elements = new List<HtmlElementContext>();
        var hasStylesheetDrivenWhitespace = ContainsHtmlStartTag(html, "style")
            || ContainsHtmlStartTag(html, "link");
        var index = 0;
        var lastCancellationCheckIndex = 0;
        var pendingCollapsibleSpace = false;
        var hasInlineContent = false;
        while (index < html.Length)
        {
            if (index - lastCancellationCheckIndex >= 4096)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastCancellationCheckIndex = index;
            }
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
                        decodeCharacterReferences: context.TagName == "textarea",
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
                if (hasStylesheetDrivenWhitespace)
                {
                    mode = HtmlWhiteSpaceMode.Preserve;
                }
                AppendHtmlText(
                    html.AsSpan(index, textEnd - index),
                    mode,
                    decodeCharacterReferences: true,
                    normalized,
                    ref pendingCollapsibleSpace,
                    ref hasInlineContent);
                index = textEnd;
                continue;
            }

            var tagEnd = FindHtmlTagEnd(html, index);
            var tag = html.AsSpan(index, tagEnd - index);
            var tagName = GetHtmlTagName(tag, out var isClosing, out var hasSelfClosingSyntax);
            if (isClosing && IsVoidElement(tagName))
            {
                index = tagEnd;
                continue;
            }
            var parentIsForeign = elements.Count > 0 && elements[^1].IsForeignContent;
            var isForeign = parentIsForeign || (!isClosing && tagName is "svg" or "math");
            var isSelfClosing = IsVoidElement(tagName) || (isForeign && hasSelfClosingSyntax);
            var isBlock = IsBlockElement(tagName);
            var isWhitespaceBoundary = isBlock || tagName == "br";
            var exposesWhitespaceBoundary = isClosing
                ? FindHtmlElement(elements, tagName)?.ExposesWhitespaceBoundary == true
                : HtmlTagExposesWhitespaceBoundary(tagName, tag);
            if (isWhitespaceBoundary)
            {
                pendingCollapsibleSpace = false;
                hasInlineContent = false;
            }
            else if ((isSelfClosing || exposesWhitespaceBoundary)
                     && pendingCollapsibleSpace
                     && hasInlineContent)
            {
                normalized.Append(' ');
                pendingCollapsibleSpace = false;
            }

            normalized.Append(NormalizeHtmlTagSyntax(
                tag,
                tagName,
                isClosing,
                isSelfClosing,
                isForeign));
            if (isClosing)
            {
                PopHtmlElement(elements, tagName);
                hasInlineContent |= IsInlineBoxElement(tagName);
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
                    IsRawTextElement(tagName),
                    isForeign,
                    exposesWhitespaceBoundary));
            }
            else if (!isWhitespaceBoundary)
            {
                hasInlineContent = true;
            }

            index = tagEnd;
        }

        return normalized.ToString().Trim();
    }

    private static bool HtmlTagExposesWhitespaceBoundary(
        string tagName,
        ReadOnlySpan<char> tag)
        => tagName is "a" or "b" or "button" or "code" or "del" or "em" or "i" or "ins"
            or "kbd" or "mark" or "q" or "s" or "samp" or "small" or "strike"
            or "strong" or "sub" or "sup" or "u" or "var"
            || HasHtmlAttributes(tag);

    private static bool HasHtmlAttributes(ReadOnlySpan<char> tag)
    {
        var index = 1;
        while (index < tag.Length && IsCollapsibleHtmlWhitespace(tag[index]))
        {
            index++;
        }
        if (index < tag.Length && tag[index] == '/')
        {
            index++;
        }
        while (index < tag.Length
               && !IsCollapsibleHtmlWhitespace(tag[index])
               && tag[index] is not '>' and not '/')
        {
            index++;
        }
        while (index < tag.Length && IsCollapsibleHtmlWhitespace(tag[index]))
        {
            index++;
        }
        return index < tag.Length && tag[index] is not '>' and not '/';
    }

    private static bool ContainsHtmlStartTag(string html, string tagName)
    {
        var search = $"<{tagName}";
        var index = 0;
        while ((index = html.IndexOf(search, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var afterName = index + search.Length;
            if (afterName >= html.Length
                || html[afterName] == '>'
                || html[afterName] == '/'
                || IsCollapsibleHtmlWhitespace(html[afterName]))
            {
                return true;
            }
            index = afterName;
        }
        return false;
    }

    private static void AppendHtmlText(
        ReadOnlySpan<char> text,
        HtmlWhiteSpaceMode mode,
        bool decodeCharacterReferences,
        StringBuilder normalized,
        ref bool pendingCollapsibleSpace,
        ref bool hasInlineContent)
    {
        var decodedText = decodeCharacterReferences
            ? DecodeHtmlCharacterReferences(text.ToString())
            : text.ToString();
        text = decodedText.AsSpan();
        if (mode == HtmlWhiteSpaceMode.Preserve)
        {
            if (pendingCollapsibleSpace && hasInlineContent)
            {
                normalized.Append(' ');
            }
            pendingCollapsibleSpace = false;
            AppendEscapedHtmlText(normalized, text);
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
                AppendEscapedHtmlTextCharacter(normalized, current);
                hasInlineContent = true;
                previousWasCarriageReturn = false;
            }
        }
    }

    private static void AppendEscapedHtmlText(
        StringBuilder normalized,
        ReadOnlySpan<char> text)
    {
        foreach (var current in text)
        {
            AppendEscapedHtmlTextCharacter(normalized, current);
        }
    }

    private static void AppendEscapedHtmlTextCharacter(
        StringBuilder normalized,
        char value)
    {
        switch (value)
        {
            case '&':
                normalized.Append("&amp;");
                break;
            case '<':
                normalized.Append("&lt;");
                break;
            case '>':
                normalized.Append("&gt;");
                break;
            default:
                normalized.Append(value);
                break;
        }
    }

    private static bool IsCollapsibleHtmlWhitespace(char value)
        => value is ' ' or '\t' or '\n' or '\f' or '\r';

    private static HtmlWhiteSpaceMode ResolveWhiteSpaceMode(
        string tagName,
        ReadOnlySpan<char> tag,
        HtmlWhiteSpaceMode inheritedMode)
    {
        var mode = tagName is "pre" or "textarea" or "script" or "style"
            ? HtmlWhiteSpaceMode.Preserve
            : inheritedMode;
        var style = GetHtmlAttributeValue(tag, "style");
        if (style is null)
        {
            return mode;
        }

        var styleWithoutComments = RemoveCssComments(style);
        var winningDeclarationIsImportant = false;
        foreach (var declaration in SplitCssDeclarations(styleWithoutComments))
        {
            var separator = declaration.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            if (TryResolveWhiteSpaceDeclaration(
                    declaration.AsSpan(0, separator).Trim(),
                    declaration.AsSpan(separator + 1),
                    out var declaredMode,
                    out var declarationIsImportant)
                && (!winningDeclarationIsImportant || declarationIsImportant))
            {
                mode = declaredMode;
                winningDeclarationIsImportant = declarationIsImportant;
            }
        }
        return mode;
    }

    private static IEnumerable<string> SplitCssDeclarations(string style)
    {
        var declarationStart = 0;
        var quote = '\0';
        var escaping = false;
        var nestingDepth = 0;
        for (var index = 0; index < style.Length; index++)
        {
            var current = style[index];
            if (escaping)
            {
                escaping = false;
                continue;
            }
            if (current == '\\')
            {
                escaping = true;
                continue;
            }
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (current is '(' or '[' or '{')
            {
                nestingDepth++;
                continue;
            }
            if (current is ')' or ']' or '}')
            {
                nestingDepth = Math.Max(0, nestingDepth - 1);
                continue;
            }
            if (current == ';' && nestingDepth == 0)
            {
                yield return style[declarationStart..index];
                declarationStart = index + 1;
            }
        }
        yield return style[declarationStart..];
    }

    private static string RemoveCssComments(string value)
    {
        var commentStart = value.IndexOf("/*", StringComparison.Ordinal);
        if (commentStart < 0)
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        var quote = '\0';
        var escaping = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (quote != '\0')
            {
                result.Append(current);
                if (escaping)
                {
                    escaping = false;
                }
                else if (current == '\\')
                {
                    escaping = true;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                result.Append(current);
                continue;
            }
            if (current == '/' && index + 1 < value.Length && value[index + 1] == '*')
            {
                var commentEnd = value.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    break;
                }
                index = commentEnd + 1;
                continue;
            }
            result.Append(current);
        }
        return result.ToString();
    }

    private static bool TryResolveWhiteSpaceDeclaration(
        ReadOnlySpan<char> property,
        ReadOnlySpan<char> value,
        out HtmlWhiteSpaceMode mode,
        out bool isImportant)
    {
        value = StripImportant(value, out isImportant);
        if (property.Equals("white-space-collapse", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Equals("collapse", StringComparison.OrdinalIgnoreCase)
                || value.Equals("discard", StringComparison.OrdinalIgnoreCase))
            {
                mode = HtmlWhiteSpaceMode.Collapse;
                return true;
            }
            if (value.Equals("preserve-breaks", StringComparison.OrdinalIgnoreCase))
            {
                mode = HtmlWhiteSpaceMode.PreLine;
                return true;
            }
            if (value.Equals("preserve", StringComparison.OrdinalIgnoreCase)
                || value.Equals("preserve-spaces", StringComparison.OrdinalIgnoreCase)
                || value.Equals("break-spaces", StringComparison.OrdinalIgnoreCase))
            {
                mode = HtmlWhiteSpaceMode.Preserve;
                return true;
            }
            if (ContainsComputedCssValue(value))
            {
                mode = HtmlWhiteSpaceMode.Preserve;
                return true;
            }
            mode = default;
            isImportant = false;
            return false;
        }
        else if (!property.Equals("white-space", StringComparison.OrdinalIgnoreCase))
        {
            mode = default;
            isImportant = false;
            return false;
        }

        if (TryResolveWhiteSpaceValue(value, out mode))
        {
            return true;
        }
        if (ContainsComputedCssValue(value))
        {
            mode = HtmlWhiteSpaceMode.Preserve;
            return true;
        }

        mode = default;
        isImportant = false;
        return false;
    }

    private static ReadOnlySpan<char> StripImportant(
        ReadOnlySpan<char> value,
        out bool isImportant)
    {
        value = value.Trim();
        var importantSeparator = value.LastIndexOf('!');
        isImportant = importantSeparator >= 0
            && value[(importantSeparator + 1)..].Trim().Equals(
                "important",
                StringComparison.OrdinalIgnoreCase);
        if (isImportant)
        {
            value = value[..importantSeparator].TrimEnd();
        }
        return value;
    }

    private static bool TryResolveWhiteSpaceValue(
        ReadOnlySpan<char> value,
        out HtmlWhiteSpaceMode mode)
    {
        if (value.Equals("normal", StringComparison.OrdinalIgnoreCase)
            || value.Equals("nowrap", StringComparison.OrdinalIgnoreCase)
            || value.Equals("collapse wrap", StringComparison.OrdinalIgnoreCase)
            || value.Equals("collapse nowrap", StringComparison.OrdinalIgnoreCase))
        {
            mode = HtmlWhiteSpaceMode.Collapse;
            return true;
        }
        if (value.Equals("pre-line", StringComparison.OrdinalIgnoreCase)
            || value.Equals("preserve-breaks", StringComparison.OrdinalIgnoreCase)
            || value.Equals("preserve-breaks wrap", StringComparison.OrdinalIgnoreCase)
            || value.Equals("preserve-breaks nowrap", StringComparison.OrdinalIgnoreCase))
        {
            mode = HtmlWhiteSpaceMode.PreLine;
            return true;
        }
        if (value.Equals("pre", StringComparison.OrdinalIgnoreCase)
            || value.Equals("pre-wrap", StringComparison.OrdinalIgnoreCase)
            || value.Equals("break-spaces", StringComparison.OrdinalIgnoreCase)
            || value.Equals("preserve", StringComparison.OrdinalIgnoreCase)
            || value.Equals("preserve nowrap", StringComparison.OrdinalIgnoreCase)
            || value.Equals("preserve wrap", StringComparison.OrdinalIgnoreCase)
            || value.Equals("break-spaces nowrap", StringComparison.OrdinalIgnoreCase)
            || value.Equals("break-spaces wrap", StringComparison.OrdinalIgnoreCase))
        {
            mode = HtmlWhiteSpaceMode.Preserve;
            return true;
        }

        mode = default;
        return false;
    }

    private static bool ContainsComputedCssValue(ReadOnlySpan<char> value)
        => value.Contains("var(", StringComparison.OrdinalIgnoreCase)
            || value.Contains("env(", StringComparison.OrdinalIgnoreCase);

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
                       && tag[index] != '>')
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

    private static HtmlElementContext? FindHtmlElement(
        List<HtmlElementContext> elements,
        string tagName)
    {
        for (var index = elements.Count - 1; index >= 0; index--)
        {
            if (string.Equals(elements[index].TagName, tagName, StringComparison.Ordinal))
            {
                return elements[index];
            }
        }
        return null;
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

    private static bool IsInlineBoxElement(string tagName)
        => tagName is "audio" or "button" or "canvas" or "iframe" or "meter"
            or "object" or "progress" or "select" or "textarea" or "video";

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

    private static string NormalizeHtmlTagSyntax(
        ReadOnlySpan<char> tag,
        string tagName,
        bool isClosing,
        bool isSelfClosing,
        bool isForeign)
    {
        if (tagName.Length == 0)
        {
            return CollapseHtmlTagWhitespace(tag);
        }

        var index = 1;
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

        var originalTagName = tag[nameStart..index].ToString();
        var canonicalTagName = isForeign ? originalTagName : tagName;
        if (isClosing)
        {
            return $"</{canonicalTagName}>";
        }

        var normalized = new StringBuilder(tag.Length);
        normalized.Append('<').Append(canonicalTagName);
        var attributes = new List<(string Name, string? Value)>();
        var attributeNames = new HashSet<string>(
            isForeign ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        while (index < tag.Length)
        {
            while (index < tag.Length && IsCollapsibleHtmlWhitespace(tag[index]))
            {
                index++;
            }
            if (index >= tag.Length || tag[index] is '>' or '/')
            {
                break;
            }

            var attributeNameStart = index;
            while (index < tag.Length
                   && !IsCollapsibleHtmlWhitespace(tag[index])
                   && tag[index] is not '=' and not '>' and not '/')
            {
                index++;
            }
            var attributeName = tag[attributeNameStart..index].ToString();
            while (index < tag.Length && IsCollapsibleHtmlWhitespace(tag[index]))
            {
                index++;
            }

            string? attributeValue = string.Empty;
            if (index < tag.Length && tag[index] == '=')
            {
                index++;
                while (index < tag.Length && IsCollapsibleHtmlWhitespace(tag[index]))
                {
                    index++;
                }
                var quote = index < tag.Length && tag[index] is '\'' or '"' ? tag[index++] : '\0';
                var valueStart = index;
                if (quote == '\0')
                {
                    while (index < tag.Length
                           && !IsCollapsibleHtmlWhitespace(tag[index])
                           && tag[index] is not '>')
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
                attributeValue = tag[valueStart..index].ToString();
                if (quote != '\0' && index < tag.Length)
                {
                    index++;
                }
            }

            var canonicalAttributeName = isForeign
                ? attributeName
                : attributeName.ToLowerInvariant();
            if (attributeNames.Add(canonicalAttributeName))
            {
                var canonicalAttributeValue = attributeValue is null
                    ? null
                    : DecodeHtmlCharacterReferences(attributeValue);
                if (!isForeign
                    && (IsHtmlBooleanAttribute(canonicalAttributeName)
                        || (canonicalAttributeName == "hidden"
                            && !string.Equals(
                                canonicalAttributeValue,
                                "until-found",
                                StringComparison.OrdinalIgnoreCase))))
                {
                    canonicalAttributeValue = null;
                }
                attributes.Add((
                    canonicalAttributeName,
                    canonicalAttributeValue));
            }
        }
        foreach (var attribute in attributes.OrderBy(static attribute => attribute.Name, StringComparer.Ordinal))
        {
            normalized.Append(' ').Append(attribute.Name);
            if (attribute.Value is not null)
            {
                normalized
                    .Append("=\"")
                    .Append(WebUtility.HtmlEncode(attribute.Value))
                    .Append('"');
            }
        }
        normalized.Append(isSelfClosing && isForeign ? "/>" : ">");
        return normalized.ToString();
    }

    private static bool IsHtmlBooleanAttribute(string attributeName)
        => attributeName is "allowfullscreen" or "async" or "autofocus" or "autoplay"
            or "checked" or "controls" or "default" or "defer" or "disabled"
            or "formnovalidate" or "inert" or "ismap" or "itemscope" or "loop"
            or "multiple" or "muted" or "nomodule" or "novalidate" or "open"
            or "playsinline" or "readonly" or "required" or "reversed" or "selected";

    private static string DecodeHtmlCharacterReferences(string value)
    {
        var decoded = new StringBuilder(value.Length);
        var segmentStart = 0;
        var index = 0;
        while (index < value.Length)
        {
            if (value[index] != '&')
            {
                index++;
                continue;
            }
            if (!TryDecodeNumericCharacterReference(
                    value.AsSpan(index),
                    out var consumed,
                    out var replacement)
                && !TryDecodeNamedCharacterReference(
                    value.AsSpan(index),
                    out consumed,
                    out replacement))
            {
                index++;
                continue;
            }

            if (index > segmentStart)
            {
                decoded.Append(WebUtility.HtmlDecode(value[segmentStart..index]));
            }
            decoded.Append(replacement);
            index += consumed;
            segmentStart = index;
        }

        if (segmentStart < value.Length)
        {
            decoded.Append(WebUtility.HtmlDecode(value[segmentStart..]));
        }
        return decoded.ToString();
    }

    private static bool TryDecodeNamedCharacterReference(
        ReadOnlySpan<char> value,
        out int consumed,
        out string replacement)
    {
        consumed = 0;
        replacement = string.Empty;
        if (value.Length < 3 || value[0] != '&')
        {
            return false;
        }

        var semicolon = 1;
        while (semicolon < value.Length && char.IsAsciiLetterOrDigit(value[semicolon]))
        {
            semicolon++;
        }
        if (semicolon < 2 || semicolon >= value.Length || value[semicolon] != ';')
        {
            return false;
        }

        var decoded = EntityHelper.DecodeEntity(value[1..semicolon]);
        if (decoded is null)
        {
            return false;
        }
        consumed = semicolon + 1;
        replacement = decoded;
        return true;
    }

    private static bool TryDecodeNumericCharacterReference(
        ReadOnlySpan<char> value,
        out int consumed,
        out string replacement)
    {
        consumed = 0;
        replacement = string.Empty;
        if (value.Length < 3 || value[0] != '&' || value[1] != '#')
        {
            return false;
        }

        var index = 2;
        var isHexadecimal = index < value.Length && value[index] is 'x' or 'X';
        if (isHexadecimal)
        {
            index++;
        }
        var digitsStart = index;
        var codePoint = 0;
        while (index < value.Length)
        {
            var digit = isHexadecimal
                ? GetHexadecimalDigit(value[index])
                : value[index] is >= '0' and <= '9'
                    ? value[index] - '0'
                    : -1;
            if (digit < 0)
            {
                break;
            }
            codePoint = codePoint > 0x10FFFF
                ? codePoint
                : (codePoint * (isHexadecimal ? 16 : 10)) + digit;
            index++;
        }
        if (index == digitsStart)
        {
            return false;
        }
        if (index < value.Length && value[index] == ';')
        {
            index++;
        }

        codePoint = NormalizeHtmlNumericCodePoint(codePoint);
        consumed = index;
        replacement = char.ConvertFromUtf32(codePoint);
        return true;
    }

    private static int GetHexadecimalDigit(char value)
        => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };

    private static int NormalizeHtmlNumericCodePoint(int codePoint)
        => codePoint switch
        {
            0 or > 0x10FFFF or >= 0xD800 and <= 0xDFFF => 0xFFFD,
            0x80 => 0x20AC,
            0x82 => 0x201A,
            0x83 => 0x0192,
            0x84 => 0x201E,
            0x85 => 0x2026,
            0x86 => 0x2020,
            0x87 => 0x2021,
            0x88 => 0x02C6,
            0x89 => 0x2030,
            0x8A => 0x0160,
            0x8B => 0x2039,
            0x8C => 0x0152,
            0x8E => 0x017D,
            0x91 => 0x2018,
            0x92 => 0x2019,
            0x93 => 0x201C,
            0x94 => 0x201D,
            0x95 => 0x2022,
            0x96 => 0x2013,
            0x97 => 0x2014,
            0x98 => 0x02DC,
            0x99 => 0x2122,
            0x9A => 0x0161,
            0x9B => 0x203A,
            0x9C => 0x0153,
            0x9E => 0x017E,
            0x9F => 0x0178,
            _ => codePoint,
        };

    private static string CollapseHtmlTagWhitespace(ReadOnlySpan<char> tag)
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
