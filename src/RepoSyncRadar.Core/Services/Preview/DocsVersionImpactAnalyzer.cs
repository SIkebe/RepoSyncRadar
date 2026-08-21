using System.Globalization;
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

    private enum HtmlElementNamespace
    {
        Html,
        Svg,
        MathMl,
    }

    private enum HtmlForeignIntegrationKind
    {
        None,
        Html,
        MathText,
    }

    private sealed record HtmlElementContext(
        string TagName,
        HtmlWhiteSpaceMode WhiteSpaceMode,
        bool IsRawText,
        HtmlElementNamespace Namespace,
        bool ExposesWhitespaceBoundary,
        bool MayRenderEmptyInlineBox = false,
        bool IsImplied = false,
        HtmlForeignIntegrationKind IntegrationKind = HtmlForeignIntegrationKind.None)
    {
        public bool IsForeignContent => Namespace != HtmlElementNamespace.Html;
    }

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
        var normalizationCache = new Dictionary<(string Rendered, string RepoPath), string>();
        string NormalizeCached(string? rendered, string? repoPath)
        {
            var cacheKey = (
                Rendered: rendered ?? string.Empty,
                RepoPath: repoPath ?? string.Empty);
            if (!normalizationCache.TryGetValue(cacheKey, out var normalized))
            {
                normalized = NormalizeForComparison(
                    cacheKey.Rendered,
                    cacheKey.RepoPath,
                    cancellationToken);
                normalizationCache.Add(cacheKey, normalized);
            }
            return normalized;
        }
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
            if (string.Equals(beforeRendered, afterRendered, StringComparison.Ordinal)
                && string.Equals(beforeRepoPath, afterRepoPath, StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.Equals(
                    NormalizeCached(beforeRendered, beforeRepoPath),
                    NormalizeCached(afterRendered, afterRepoPath),
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
            if (string.Equals(beforeRendered, afterRendered, StringComparison.Ordinal)
                && string.Equals(beforeRepoPath, afterRepoPath, StringComparison.Ordinal))
            {
                continue;
            }
            var beforeNormalized = NormalizeForComparison(
                beforeRendered,
                beforeRepoPath,
                cancellationToken);
            var afterNormalized = NormalizeForComparison(
                afterRendered,
                afterRepoPath,
                cancellationToken);
            if (!string.Equals(beforeNormalized, afterNormalized, StringComparison.Ordinal))
            {
                var changes = BuildChangeSnippets(
                    beforeRendered,
                    afterRendered,
                    cancellationToken);
                if (changes.Count == 0)
                {
                    changes.Add(new DocsVersionChangeSnippet(
                        DocsVersionChangeKind.Updated,
                        TrimExcerpt(beforeNormalized),
                        TrimExcerpt(afterNormalized)));
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

    private static List<DocsVersionChangeSnippet> BuildChangeSnippets(
        string? beforeRendered,
        string? afterRendered,
        CancellationToken cancellationToken)
    {
        var beforeBlocks = SplitBlocks(beforeRendered);
        var afterBlocks = SplitBlocks(afterRendered);
        cancellationToken.ThrowIfCancellationRequested();
        var snippets = new List<DocsVersionChangeSnippet>();
        var removed = new List<string>();
        var added = new List<string>();
        var table = BuildLcsTable(beforeBlocks, afterBlocks, cancellationToken);
        var beforeIndex = 0;
        var afterIndex = 0;

        while (beforeIndex < beforeBlocks.Count || afterIndex < afterBlocks.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeIndex < beforeBlocks.Count
                && afterIndex < afterBlocks.Count
                && string.Equals(beforeBlocks[beforeIndex], afterBlocks[afterIndex], StringComparison.Ordinal))
            {
                FlushPending(snippets, removed, added, cancellationToken);
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

        FlushPending(snippets, removed, added, cancellationToken);
        return snippets;
    }

    private static int[,] BuildLcsTable(
        IReadOnlyList<string> beforeBlocks,
        IReadOnlyList<string> afterBlocks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var table = new int[beforeBlocks.Count + 1, afterBlocks.Count + 1];
        for (var beforeIndex = beforeBlocks.Count - 1; beforeIndex >= 0; beforeIndex--)
        {
            for (var afterIndex = afterBlocks.Count - 1; afterIndex >= 0; afterIndex--)
            {
                if ((afterIndex & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
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
        List<string> added,
        CancellationToken cancellationToken)
    {
        var pairCount = Math.Min(removed.Count, added.Count);
        for (var index = 0; index < pairCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snippets.Add(new DocsVersionChangeSnippet(
                DocsVersionChangeKind.Updated,
                TrimExcerpt(removed[index]),
                TrimExcerpt(added[index])));
        }

        for (var index = pairCount; index < removed.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snippets.Add(new DocsVersionChangeSnippet(
                DocsVersionChangeKind.Removed,
                TrimExcerpt(removed[index]),
                null));
        }

        for (var index = pairCount; index < added.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        string? repoPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preprocessed = MarkdownPreviewRenderer.PreprocessMarkdownForComparison(
            rendered ?? string.Empty,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var html = Markdown.ToHtml(preprocessed, _comparisonPipeline);
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            html = MarkdownPreviewRenderer.RewriteLocalReferencesForComparison(
                html,
                repoPath);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = new StringBuilder(html.Length);
        var elements = new List<HtmlElementContext>();
        var hasStylesheetDrivenWhitespace = ContainsHtmlStartTag(html, "style")
            || ContainsStylesheetLink(html);
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
                var closingTag = context.TagName == "plaintext"
                    ? -1
                    : FindRawTextClosingTag(html, index, context.TagName);
                if (closingTag != index)
                {
                    var textEnd = closingTag < 0 ? html.Length : closingTag;
                    if (context.TagName != "iframe")
                    {
                        AppendHtmlText(
                            html.AsSpan(index, textEnd - index),
                            context.WhiteSpaceMode,
                            decodeCharacterReferences: IsRcDataElement(context.TagName),
                            normalized,
                            ref pendingCollapsibleSpace,
                            ref hasInlineContent);
                    }
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
            var elementNamespace = ResolveHtmlElementNamespace(
                elements,
                tagName,
                tag,
                isClosing);
            var isForeign = elementNamespace != HtmlElementNamespace.Html;
            if (isClosing && !isForeign && IsVoidElement(tagName))
            {
                index = tagEnd;
                continue;
            }
            if (!isForeign)
            {
                CloseImpliedTableDescendants(elements, tagName, isClosing);
                CloseOpenTableSectionIfNeeded(
                    elements,
                    normalized,
                    tagName,
                    isClosing);
                if (!isClosing)
                {
                    ApplyImpliedHtmlEndTags(elements, tagName);
                }
            }
            OpenImpliedTableBodyIfNeeded(
                elements,
                normalized,
                tagName,
                isClosing,
                isForeign);
            var isSelfClosing = !isForeign && IsVoidElement(tagName)
                || isForeign && hasSelfClosingSyntax;
            var isBlock = IsBlockElement(tagName);
            var isWhitespaceBoundary = isBlock || tagName == "br";
            var matchingElement = isClosing
                ? FindHtmlElement(elements, tagName, elementNamespace)
                : null;
            var exposesWhitespaceBoundary = isClosing
                ? matchingElement?.ExposesWhitespaceBoundary == true
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

            if (!IsOptionalHtmlEndTagSyntax(
                    tagName,
                    isClosing,
                    isForeign,
                    html,
                    tagEnd,
                    elements))
            {
                normalized.Append(NormalizeHtmlTagSyntax(
                    tag,
                    tagName,
                    isClosing,
                    isSelfClosing,
                    isForeign));
            }
            if (isClosing)
            {
                PopHtmlElement(elements, tagName, elementNamespace);
                hasInlineContent |= IsInlineBoxElement(tagName)
                    || matchingElement?.MayRenderEmptyInlineBox == true;
            }
            else if (!isSelfClosing)
            {
                var inheritedMode = elements.Count == 0
                    ? HtmlWhiteSpaceMode.Collapse
                    : elements[^1].WhiteSpaceMode;
                var whiteSpaceMode = ResolveWhiteSpaceMode(tagName, tag, inheritedMode);
                elements.Add(new HtmlElementContext(
                    tagName,
                    whiteSpaceMode,
                    !isForeign && IsRawTextElement(tagName),
                    elementNamespace,
                    exposesWhitespaceBoundary,
                    MayRenderEmptyInlineBox(tagName, tag),
                    IntegrationKind: GetHtmlForeignIntegrationKind(
                        elementNamespace,
                        tagName,
                        tag)));
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
        => IsInlineBoxElement(tagName)
            || tagName is "a" or "b" or "button" or "code" or "del" or "em" or "i" or "ins"
            or "kbd" or "mark" or "q" or "s" or "samp" or "small" or "strike"
            or "strong" or "sub" or "sup" or "u" or "var"
            || HasHtmlAttributes(tag);

    private static bool MayRenderEmptyInlineBox(string tagName, ReadOnlySpan<char> tag)
    {
        if (IsBlockElement(tagName))
        {
            return false;
        }

        var style = GetHtmlAttributeValue(tag, "style");
        if (style is null)
        {
            return false;
        }

        var styleWithoutComments = RemoveCssComments(DecodeHtmlCharacterReferences(style));
        var effectiveDeclarations = new Dictionary<string, (string Value, bool IsImportant)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in SplitCssDeclarations(styleWithoutComments))
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var propertyName = declaration[..separator].Trim().ToLowerInvariant();
            if (!IsEmptyBoxAffectingProperty(propertyName))
            {
                continue;
            }

            var value = StripImportant(declaration.AsSpan(separator + 1), out var isImportant)
                .ToString();
            if (!effectiveDeclarations.TryGetValue(propertyName, out var current)
                || !current.IsImportant
                || isImportant)
            {
                effectiveDeclarations[propertyName] = (value, isImportant);
            }
        }

        foreach (var (propertyName, declaration) in effectiveDeclarations)
        {
            if (propertyName == "display")
            {
                if (DisplayCreatesEmptyBox(declaration.Value))
                {
                    return true;
                }
                continue;
            }
            if (CssBoxValueCreatesSpace(propertyName, declaration.Value))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEmptyBoxAffectingProperty(string propertyName)
        => propertyName == "display"
            || propertyName is "margin" or "padding" or "border"
            || propertyName.StartsWith("margin-", StringComparison.Ordinal)
            || propertyName.StartsWith("padding-", StringComparison.Ordinal)
            || propertyName.StartsWith("border-", StringComparison.Ordinal)
                && !propertyName.Contains("color", StringComparison.Ordinal)
                && !propertyName.Contains("image", StringComparison.Ordinal)
                && !propertyName.Contains("radius", StringComparison.Ordinal);

    private static bool DisplayCreatesEmptyBox(string value)
    {
        var normalized = NormalizeCssWhitespace(value.AsSpan()).ToLowerInvariant();
        return normalized is not ("" or "none" or "contents" or "inline" or "inline flow"
            or "initial" or "unset" or "revert" or "revert-layer");
    }

    private static bool CssBoxValueCreatesSpace(string propertyName, string value)
    {
        var normalized = NormalizeCssWhitespace(value.AsSpan()).ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }
        var tokens = normalized.Split(
            [' ', '/', ','],
            StringSplitOptions.RemoveEmptyEntries);
        if (propertyName.StartsWith("border", StringComparison.Ordinal)
            && tokens.Any(static token => token is "none" or "hidden"))
        {
            return false;
        }
        if (tokens.Any(static token => token.StartsWith("calc(", StringComparison.Ordinal)
                || token.StartsWith("var(", StringComparison.Ordinal)
                || token.StartsWith("env(", StringComparison.Ordinal)))
        {
            return true;
        }

        var foundNumericValue = false;
        foreach (var token in tokens)
        {
            if (!TryGetCssNumericValue(token, out var numericValue))
            {
                continue;
            }
            foundNumericValue = true;
            if (numericValue != 0)
            {
                return true;
            }
        }
        if (foundNumericValue)
        {
            return false;
        }
        return propertyName.StartsWith("border", StringComparison.Ordinal)
            && tokens.Any(static token => token is "solid" or "dashed" or "dotted" or "double"
                or "groove" or "ridge" or "inset" or "outset");
    }

    private static bool TryGetCssNumericValue(string token, out double value)
    {
        value = default;
        var numberEnd = 0;
        while (numberEnd < token.Length
               && (char.IsDigit(token[numberEnd]) || token[numberEnd] is '+' or '-' or '.'))
        {
            numberEnd++;
        }
        return numberEnd > 0
            && double.TryParse(
                token.AsSpan(0, numberEnd),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static void OpenImpliedTableBodyIfNeeded(
        List<HtmlElementContext> elements,
        StringBuilder normalized,
        string tagName,
        bool isClosing,
        bool isForeign)
    {
        if (isClosing
            || isForeign
            || tagName != "tr"
            || elements.Count == 0
            || elements[^1].TagName != "table")
        {
            return;
        }

        normalized.Append("<tbody>");
        elements.Add(new HtmlElementContext(
            "tbody",
            elements[^1].WhiteSpaceMode,
            IsRawText: false,
            Namespace: HtmlElementNamespace.Html,
            ExposesWhitespaceBoundary: false,
            IsImplied: true));
    }

    private static void CloseOpenTableSectionIfNeeded(
        List<HtmlElementContext> elements,
        StringBuilder normalized,
        string tagName,
        bool isClosing)
    {
        var endsCurrentRowGroup = isClosing && tagName == "table"
            || !isClosing && tagName is "caption" or "colgroup" or "tbody" or "tfoot" or "thead";
        if (!endsCurrentRowGroup
            || elements.Count == 0
            || elements[^1].TagName is not ("tbody" or "tfoot" or "thead"))
        {
            return;
        }

        normalized.Append("</").Append(elements[^1].TagName).Append('>');
        elements.RemoveAt(elements.Count - 1);
    }

    private static void CloseImpliedTableDescendants(
        List<HtmlElementContext> elements,
        string tagName,
        bool isClosing)
    {
        var opensNewTableSection = !isClosing
            && tagName is "caption" or "colgroup" or "tbody" or "tfoot" or "thead";
        if (!opensNewTableSection
            && (!isClosing || tagName is not ("tr" or "tbody" or "tfoot" or "thead" or "table")))
        {
            return;
        }

        RemoveOpenTableElement(elements, static name => name is "td" or "th", "tr");
        if (!isClosing || tagName != "tr")
        {
            RemoveOpenTableElement(
                elements,
                static name => name == "tr",
                "tbody",
                "tfoot",
                "thead",
                "table");
        }
    }

    private static void RemoveOpenTableElement(
        List<HtmlElementContext> elements,
        Func<string, bool> matches,
        params string[] boundaries)
    {
        for (var index = elements.Count - 1; index >= 0; index--)
        {
            var tagName = elements[index].TagName;
            if (matches(tagName))
            {
                elements.RemoveRange(index, elements.Count - index);
                return;
            }
            if (boundaries.Contains(tagName, StringComparer.Ordinal))
            {
                return;
            }
        }
    }

    private static bool IsOptionalHtmlEndTagSyntax(
        string tagName,
        bool isClosing,
        bool isForeign,
        string html,
        int nextIndex,
        IReadOnlyList<HtmlElementContext> elements)
    {
        if (isForeign || !isClosing || tagName is not ("li" or "p" or "td" or "th" or "tr"))
        {
            return false;
        }

        string? parentTagName = null;
        for (var i = elements.Count - 1; i >= 0; i--)
        {
            if (elements[i].TagName != tagName)
            {
                continue;
            }

            if (i > 0)
            {
                parentTagName = elements[i - 1].TagName;
            }
            break;
        }
        if (tagName == "li" && parentTagName is not ("ol" or "ul"))
        {
            return false;
        }
        if (tagName is "td" or "th" && parentTagName != "tr")
        {
            return false;
        }
        if (tagName == "tr" && parentTagName is not ("tbody" or "tfoot" or "thead"))
        {
            return false;
        }

        while (nextIndex < html.Length)
        {
            while (nextIndex < html.Length
                   && IsCollapsibleHtmlWhitespace(html[nextIndex]))
            {
                nextIndex++;
            }
            if (!html.AsSpan(nextIndex).StartsWith("<!--", StringComparison.Ordinal))
            {
                break;
            }

            var commentEnd = html.IndexOf("-->", nextIndex + 4, StringComparison.Ordinal);
            if (commentEnd < 0)
            {
                return true;
            }
            nextIndex = commentEnd + 3;
        }

        if (nextIndex >= html.Length)
        {
            return true;
        }
        if (html[nextIndex] != '<')
        {
            return false;
        }

        var nextTagEnd = FindHtmlTagEnd(html, nextIndex);
        var nextTag = html.AsSpan(nextIndex, nextTagEnd - nextIndex);
        var nextTagName = GetHtmlTagName(
            nextTag,
            out var nextIsClosing,
            out _);
        if (tagName == "li")
        {
            return !nextIsClosing && nextTagName == "li"
                || nextIsClosing && nextTagName == parentTagName;
        }
        if (tagName == "p")
        {
            return !nextIsClosing && ClosesOpenParagraph(nextTagName)
                || nextIsClosing && nextTagName == parentTagName;
        }
        if (tagName is "td" or "th")
        {
            return !nextIsClosing && nextTagName is "td" or "th"
                || nextIsClosing && nextTagName is "tr" or "tbody" or "tfoot" or "thead" or "table";
        }
        return !nextIsClosing && nextTagName == "tr"
            || nextIsClosing && nextTagName is "tbody" or "tfoot" or "thead" or "table";
    }

    private static void ApplyImpliedHtmlEndTags(
        List<HtmlElementContext> elements,
        string openingTagName)
    {
        if (openingTagName is "td" or "th")
        {
            RemoveOpenTableElement(elements, static name => name is "td" or "th", "tr");
            return;
        }
        if (openingTagName == "tr")
        {
            RemoveOpenTableElement(
                elements,
                static name => name == "tr",
                "tbody",
                "tfoot",
                "thead",
                "table");
            return;
        }
        if (openingTagName == "li")
        {
            for (var i = elements.Count - 1; i >= 0; i--)
            {
                if (elements[i].TagName == "li")
                {
                    elements.RemoveRange(i, elements.Count - i);
                    return;
                }
                if (elements[i].TagName is "ol" or "ul")
                {
                    return;
                }
            }
            return;
        }
        if (!ClosesOpenParagraph(openingTagName))
        {
            return;
        }
        for (var i = elements.Count - 1; i >= 0; i--)
        {
            if (elements[i].TagName == "p")
            {
                elements.RemoveRange(i, elements.Count - i);
                return;
            }
        }
    }

    private static bool ClosesOpenParagraph(string tagName)
        => tagName is "address" or "article" or "aside" or "blockquote" or "details"
            or "div" or "dl" or "fieldset" or "figcaption" or "figure" or "footer"
            or "form" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "header"
            or "hgroup" or "hr" or "main" or "menu" or "nav" or "ol" or "p" or "pre"
            or "search" or "section" or "table" or "ul";

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

    private static bool ContainsStylesheetLink(string html)
    {
        const string search = "<link";
        var index = 0;
        while ((index = html.IndexOf(search, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var afterName = index + search.Length;
            if (afterName < html.Length
                && html[afterName] != '>'
                && html[afterName] != '/'
                && !IsCollapsibleHtmlWhitespace(html[afterName]))
            {
                index = afterName;
                continue;
            }

            var tagEnd = FindHtmlTagEnd(html, index);
            var tag = html.AsSpan(index, tagEnd - index);
            var rel = GetHtmlAttributeValue(tag, "rel");
            if (rel is not null)
            {
                var decodedRel = DecodeHtmlCharacterReferences(rel);
                foreach (var token in decodedRel.Split(
                             [' ', '\t', '\n', '\f', '\r'],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Equals("stylesheet", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            index = tagEnd;
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
        var mode = tagName is "pre" or "textarea" or "script" or "style" or "xmp" or "plaintext"
            ? HtmlWhiteSpaceMode.Preserve
            : inheritedMode;
        var style = GetHtmlAttributeValue(tag, "style");
        if (style is null)
        {
            return mode;
        }

        var styleWithoutComments = RemoveCssComments(DecodeHtmlCharacterReferences(style));
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
        var normalizedValue = NormalizeCssWhitespace(value);
        if (normalizedValue.Equals("normal", StringComparison.OrdinalIgnoreCase)
            || normalizedValue.Equals("nowrap", StringComparison.OrdinalIgnoreCase)
            || normalizedValue.Equals("collapse", StringComparison.OrdinalIgnoreCase)
            || normalizedValue.Equals("wrap", StringComparison.OrdinalIgnoreCase))
        {
            mode = HtmlWhiteSpaceMode.Collapse;
            return true;
        }
        if (normalizedValue.Equals("pre-line", StringComparison.OrdinalIgnoreCase)
            || normalizedValue.Equals("preserve-breaks", StringComparison.OrdinalIgnoreCase))
        {
            mode = HtmlWhiteSpaceMode.PreLine;
            return true;
        }
        if (normalizedValue.Equals("pre", StringComparison.OrdinalIgnoreCase)
            || normalizedValue.Equals("pre-wrap", StringComparison.OrdinalIgnoreCase)
            || normalizedValue.Equals("break-spaces", StringComparison.OrdinalIgnoreCase)
            || normalizedValue.Equals("preserve", StringComparison.OrdinalIgnoreCase))
        {
            mode = HtmlWhiteSpaceMode.Preserve;
            return true;
        }

        var tokens = normalizedValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 2)
        {
            string? collapseKeyword = null;
            var hasWrapKeyword = false;
            foreach (var token in tokens)
            {
                if (token.Equals("wrap", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("nowrap", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasWrapKeyword)
                    {
                        mode = default;
                        return false;
                    }
                    hasWrapKeyword = true;
                }
                else if (token.Equals("collapse", StringComparison.OrdinalIgnoreCase)
                         || token.Equals("preserve-breaks", StringComparison.OrdinalIgnoreCase)
                         || token.Equals("preserve", StringComparison.OrdinalIgnoreCase)
                         || token.Equals("break-spaces", StringComparison.OrdinalIgnoreCase))
                {
                    if (collapseKeyword is not null)
                    {
                        mode = default;
                        return false;
                    }
                    collapseKeyword = token;
                }
                else
                {
                    mode = default;
                    return false;
                }
            }

            if (hasWrapKeyword && collapseKeyword is not null)
            {
                mode = collapseKeyword.Equals("collapse", StringComparison.OrdinalIgnoreCase)
                    ? HtmlWhiteSpaceMode.Collapse
                    : collapseKeyword.Equals("preserve-breaks", StringComparison.OrdinalIgnoreCase)
                        ? HtmlWhiteSpaceMode.PreLine
                        : HtmlWhiteSpaceMode.Preserve;
                return true;
            }
        }

        mode = default;
        return false;
    }

    private static string NormalizeCssWhitespace(ReadOnlySpan<char> value)
    {
        var normalized = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var current in value)
        {
            if (IsCollapsibleHtmlWhitespace(current))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }
            normalized.Append(current);
        }
        return normalized.ToString();
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

    private static HtmlElementNamespace ResolveHtmlElementNamespace(
        List<HtmlElementContext> elements,
        string tagName,
        ReadOnlySpan<char> tag,
        bool isClosing)
    {
        if (isClosing)
        {
            for (var index = elements.Count - 1; index >= 0; index--)
            {
                if (string.Equals(elements[index].TagName, tagName, StringComparison.Ordinal))
                {
                    return elements[index].Namespace;
                }
            }
            return elements.Count == 0
                ? HtmlElementNamespace.Html
                : elements[^1].Namespace;
        }

        if (elements.Count == 0 || elements[^1].Namespace == HtmlElementNamespace.Html)
        {
            return tagName switch
            {
                "svg" => HtmlElementNamespace.Svg,
                "math" => HtmlElementNamespace.MathMl,
                _ => HtmlElementNamespace.Html,
            };
        }

        if (HtmlForeignParentUsesHtmlParsing(elements[^1], tagName))
        {
            return tagName switch
            {
                "svg" => HtmlElementNamespace.Svg,
                "math" => HtmlElementNamespace.MathMl,
                _ => HtmlElementNamespace.Html,
            };
        }

        if (IsHtmlForeignContentBreakoutTag(tagName, tag))
        {
            while (elements.Count > 0 &&
                   elements[^1].Namespace != HtmlElementNamespace.Html &&
                   elements[^1].IntegrationKind == HtmlForeignIntegrationKind.None)
            {
                elements.RemoveAt(elements.Count - 1);
            }
            return HtmlElementNamespace.Html;
        }

        return elements[^1].Namespace;
    }

    private static bool HtmlForeignParentUsesHtmlParsing(
        HtmlElementContext parent,
        string tagName)
        => parent.Namespace == HtmlElementNamespace.MathMl
            && parent.TagName == "annotation-xml"
            && tagName == "svg"
            || parent.IntegrationKind switch
            {
                HtmlForeignIntegrationKind.Html => true,
                HtmlForeignIntegrationKind.MathText => tagName is not ("mglyph" or "malignmark"),
                _ => false,
            };

    private static HtmlForeignIntegrationKind GetHtmlForeignIntegrationKind(
        HtmlElementNamespace elementNamespace,
        string tagName,
        ReadOnlySpan<char> tag)
        => elementNamespace switch
        {
            HtmlElementNamespace.Svg when tagName is "foreignobject" or "desc" or "title"
                => HtmlForeignIntegrationKind.Html,
            HtmlElementNamespace.MathMl when tagName is "mi" or "mo" or "mn" or "ms" or "mtext"
                => HtmlForeignIntegrationKind.MathText,
            HtmlElementNamespace.MathMl when tagName == "annotation-xml"
                && HtmlAttributeEquals(tag, "encoding", "text/html",
                    "application/xhtml+xml")
                => HtmlForeignIntegrationKind.Html,
            _ => HtmlForeignIntegrationKind.None,
        };

    private static bool HtmlAttributeEquals(
        ReadOnlySpan<char> tag,
        string attributeName,
        params string[] expectedValues)
    {
        var value = GetHtmlAttributeValue(tag, attributeName);
        if (value is null)
        {
            return false;
        }

        var decodedValue = DecodeHtmlCharacterReferences(value);
        return expectedValues.Contains(decodedValue, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsHtmlForeignContentBreakoutTag(
        string tagName,
        ReadOnlySpan<char> tag)
        => tagName is "b" or "big" or "blockquote" or "body" or "br" or "center" or "code"
            or "dd" or "div" or "dl" or "dt" or "em" or "embed" or "h1" or "h2" or "h3"
            or "h4" or "h5" or "h6" or "head" or "hr" or "i" or "img" or "li"
            or "listing" or "menu" or "meta" or "nobr" or "ol" or "p" or "pre" or "ruby"
            or "s" or "small" or "span" or "strong" or "strike" or "sub" or "sup"
            or "table" or "tt" or "u" or "ul" or "var"
            || tagName == "font"
                && (GetHtmlAttributeValue(tag, "color") is not null
                    || GetHtmlAttributeValue(tag, "face") is not null
                    || GetHtmlAttributeValue(tag, "size") is not null);

    private static void PopHtmlElement(
        List<HtmlElementContext> elements,
        string tagName,
        HtmlElementNamespace elementNamespace)
    {
        for (var index = elements.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(elements[index].TagName, tagName, StringComparison.Ordinal)
                || elements[index].Namespace != elementNamespace)
            {
                continue;
            }

            if (elementNamespace != HtmlElementNamespace.Html
                && elements[^1].Namespace == HtmlElementNamespace.Html
                && HasHtmlScopeBarrier(elements, index))
            {
                return;
            }

            elements.RemoveRange(index, elements.Count - index);
            return;
        }
    }

    private static bool HasHtmlScopeBarrier(
        List<HtmlElementContext> elements,
        int matchingElementIndex)
    {
        for (var index = elements.Count - 1; index > matchingElementIndex; index--)
        {
            var candidate = elements[index];
            if (IsHtmlSpecialElement(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHtmlSpecialElement(HtmlElementContext element)
        => element.Namespace switch
        {
            HtmlElementNamespace.Html
                => MarkdownPreviewRenderer.IsHtmlSpecialElement(element.TagName),
            HtmlElementNamespace.Svg
                => element.TagName is "foreignobject" or "desc" or "title",
            HtmlElementNamespace.MathMl
                => element.TagName is "mi" or "mo" or "mn" or "ms"
                    or "mtext" or "annotation-xml",
            _ => false,
        };

    private static HtmlElementContext? FindHtmlElement(
        List<HtmlElementContext> elements,
        string tagName,
        HtmlElementNamespace elementNamespace)
    {
        for (var index = elements.Count - 1; index >= 0; index--)
        {
            if (string.Equals(elements[index].TagName, tagName, StringComparison.Ordinal)
                && elements[index].Namespace == elementNamespace)
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
                || html[afterName] == '/'
                || IsCollapsibleHtmlWhitespace(html[afterName]))
            {
                return index;
            }
            index = afterName;
        }
        return -1;
    }

    private static bool IsRawTextElement(string tagName)
        => IsRcDataElement(tagName)
            || tagName is "script" or "style" or "xmp" or "iframe" or "plaintext";

    private static bool IsRcDataElement(string tagName)
        => tagName is "textarea" or "title";

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
            or "pre" or "section" or "summary" or "table" or "tbody" or "td" or "tfoot" or "th"
            or "thead" or "tr" or "ul" or "xmp" or "plaintext";

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
