using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using TextMateSharp.Grammars;
using TextMateSharp.Themes;

namespace RepoSyncRadar.Core.Services.Preview;

internal static class MarkdownCodeDiffMarker
{
    private const char _start = '\u001e';
    private const char _end = '\u001f';

    public static string Wrap(string value, string markerClass)
    {
        var id = Guid.NewGuid().ToString("N");
        return string.Concat(
            _start, "RSR-CODE-DIFF:", id, ":open:", markerClass, _end,
            value,
            _start, "RSR-CODE-DIFF:", id, ":close", _end);
    }

    public static string CreateGap(string markerClass)
    {
        var id = Guid.NewGuid().ToString("N");
        return string.Concat(
            _start, "RSR-CODE-DIFF:", id, ":gap:", markerClass, _end);
    }
}

internal static class MarkdownSyntaxHighlightingExtensions
{
    public static MarkdownPipelineBuilder UseRepoSyncRadarSyntaxHighlighting(this MarkdownPipelineBuilder pipeline)
    {
        pipeline.Extensions.AddIfNotAlready(new MarkdownSyntaxHighlightingExtension());
        return pipeline;
    }
}

internal sealed class MarkdownSyntaxHighlightingExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not TextRendererBase<HtmlRenderer> htmlRenderer)
        {
            return;
        }

        var fallbackRenderer = htmlRenderer.ObjectRenderers.FindExact<CodeBlockRenderer>();
        if (fallbackRenderer is not null)
        {
            htmlRenderer.ObjectRenderers.Remove(fallbackRenderer);
        }
        else
        {
            fallbackRenderer = new CodeBlockRenderer();
        }

        htmlRenderer.ObjectRenderers.AddIfNotAlready(new MarkdownCodeBlockRenderer(fallbackRenderer));
    }
}

internal sealed partial class MarkdownCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    private static readonly RegistryOptions _lightOptions = new(ThemeName.LightPlus);
    private static readonly RegistryOptions _darkOptions = new(ThemeName.DarkPlus);
    private static readonly TextMateSharp.Registry.Registry _registry = new(_lightOptions);
    private static readonly Theme _lightTheme = _registry.GetTheme();
    private static readonly Theme _darkTheme = new TextMateSharp.Registry.Registry(_darkOptions).GetTheme();
    private static readonly Dictionary<string, string> _languageScopes = BuildLanguageScopes();
    private static readonly Lock _tokenizationLock = new();

    private readonly CodeBlockRenderer _fallbackRenderer;

    public MarkdownCodeBlockRenderer(CodeBlockRenderer fallbackRenderer)
    {
        _fallbackRenderer = fallbackRenderer;
    }

    protected override void Write(HtmlRenderer renderer, CodeBlock codeBlock)
    {
        if (codeBlock is not FencedCodeBlock fencedCodeBlock
            || codeBlock.Parser is not FencedCodeBlockParser fencedCodeBlockParser)
        {
            _fallbackRenderer.Write(renderer, codeBlock);
            return;
        }

        var languageId = NormalizeLanguageId(
            fencedCodeBlock.Info?.Replace(fencedCodeBlockParser.InfoPrefix ?? string.Empty, string.Empty, StringComparison.Ordinal)
            ?? string.Empty);
        var parsedCode = ParseDiffMarkers(ExtractCode(codeBlock));
        if (_fallbackRenderer.BlockMapping.ContainsKey(languageId)
            || _fallbackRenderer.BlocksAsDiv.Contains(languageId))
        {
            if (parsedCode.DiffRanges.Count == 0)
            {
                _fallbackRenderer.Write(renderer, codeBlock);
            }
            else
            {
                WriteMappedBlock(
                    renderer,
                    codeBlock,
                    fencedCodeBlockParser,
                    languageId,
                    parsedCode.Code,
                    GetMappedBlockDiffClass(parsedCode.DiffRanges));
            }
            return;
        }

        renderer.Write("<pre tabindex=\"0\"><code");
        renderer.WriteAttributes(codeBlock);
        renderer.Write('>');

        if (!string.Equals(languageId, "markdown", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(languageId, "md", StringComparison.OrdinalIgnoreCase)
            && _languageScopes.TryGetValue(languageId, out var scopeName))
        {
            WriteHighlightedCode(renderer, parsedCode, scopeName);
        }
        else
        {
            WritePlainCode(renderer, parsedCode);
        }

        renderer.Write("</code></pre>\n");
    }

    private void WriteMappedBlock(
        HtmlRenderer renderer,
        CodeBlock codeBlock,
        FencedCodeBlockParser parser,
        string languageId,
        string code,
        string diffClass)
    {
        renderer.EnsureLine();
        var blockName = _fallbackRenderer.BlockMapping.TryGetValue(languageId, out var mappedBlockName)
            ? mappedBlockName
            : "div";
        var infoPrefix = parser.InfoPrefix ?? FencedCodeBlockParser.DefaultInfoPrefix;
        codeBlock.GetAttributes().AddClass(diffClass);
        renderer.Write('<');
        renderer.Write(blockName);
        renderer.WriteAttributes(
            codeBlock.TryGetAttributes(),
            cssClass => cssClass.StartsWith(infoPrefix, StringComparison.Ordinal)
                ? cssClass[infoPrefix.Length..]
                : cssClass);
        renderer.Write('>');
        renderer.Write(WebUtility.HtmlEncode(code));
        renderer.Write("</");
        renderer.Write(blockName);
        renderer.WriteLine(">");
        renderer.EnsureLine();
    }

    private static string GetMappedBlockDiffClass(IReadOnlyList<DiffRange> ranges)
    {
        var className = ranges[0].ClassName;
        foreach (var range in ranges)
        {
            if (range.Length > 0)
            {
                className = range.ClassName;
                break;
            }
        }

        var separatorIndex = className.IndexOf(' ');
        return separatorIndex < 0 ? className : className[..separatorIndex];
    }

    private static Dictionary<string, string> BuildLanguageScopes()
    {
        var scopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in _lightOptions.GetAvailableLanguages())
        {
            var scope = _lightOptions.GetScopeByLanguageId(language.Id);
            if (string.IsNullOrWhiteSpace(scope))
            {
                continue;
            }

            scopes[language.Id] = scope;
            if (language.Aliases is null)
            {
                continue;
            }

            foreach (var alias in language.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    scopes.TryAdd(alias, scope);
                }
            }
        }

        AddAlias(scopes, "cs", "csharp");
        AddAlias(scopes, "golang", "go");
        AddAlias(scopes, "js", "javascript");
        AddAlias(scopes, "py", "python");
        AddAlias(scopes, "sh", "shellscript");
        AddAlias(scopes, "shell", "shellscript");
        AddAlias(scopes, "ts", "typescript");
        return scopes;
    }

    private static void AddAlias(Dictionary<string, string> scopes, string alias, string languageId)
    {
        if (scopes.TryGetValue(languageId, out var scope))
        {
            scopes[alias] = scope;
        }
    }

    private static string NormalizeLanguageId(string languageId)
    {
        var trimmed = languageId.Trim();
        var separatorIndex = trimmed.IndexOfAny([' ', '\t', '{']);
        return separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];
    }

    private static string ExtractCode(LeafBlock leafBlock)
    {
        var code = new StringBuilder();
        var lines = leafBlock.Lines.Lines ?? [];
        for (var index = 0; index < lines.Length; index++)
        {
            var slice = lines[index].Slice;
            if (slice.Text is null)
            {
                continue;
            }

            if (index > 0)
            {
                code.Append('\n');
            }
            code.Append(slice.Text, slice.Start, slice.Length);
        }
        return code.ToString();
    }

    private static ParsedCode ParseDiffMarkers(string markedCode)
    {
        var code = new StringBuilder(markedCode.Length);
        var ranges = new List<DiffRange>();
        var openMarkers = new Dictionary<string, OpenDiffMarker>(StringComparer.Ordinal);
        var cursor = 0;

        foreach (Match match in RenderedDiffMarkerRegex().Matches(markedCode))
        {
            code.Append(markedCode, cursor, match.Index - cursor);
            cursor = match.Index + match.Length;

            if (match.Groups["gap"].Success)
            {
                ranges.Add(new DiffRange(
                    code.Length,
                    0,
                    match.Groups["gapClass"].Value));
            }
            else if (match.Groups["open"].Success)
            {
                openMarkers[match.Groups["id"].Value] = new OpenDiffMarker(
                    code.Length,
                    match.Groups["openClass"].Value);
            }
            else if (openMarkers.Remove(match.Groups["id"].Value, out var openMarker))
            {
                ranges.Add(new DiffRange(
                    openMarker.Start,
                    code.Length - openMarker.Start,
                    openMarker.ClassName));
            }
            else
            {
                code.Append(match.Value);
            }
        }

        code.Append(markedCode, cursor, markedCode.Length - cursor);
        return new ParsedCode(code.ToString(), ranges);
    }

    private static void WriteHighlightedCode(HtmlRenderer renderer, ParsedCode parsedCode, string scopeName)
    {
        lock (_tokenizationLock)
        {
            var grammar = _registry.LoadGrammar(scopeName);
            if (grammar is null)
            {
                WritePlainCode(renderer, parsedCode);
                return;
            }

            IStateStack? ruleStack = null;
            var lineStart = 0;
            while (lineStart <= parsedCode.Code.Length)
            {
                var lineEnd = parsedCode.Code.IndexOf('\n', lineStart);
                var hasLineBreak = lineEnd >= 0;
                if (!hasLineBreak)
                {
                    lineEnd = parsedCode.Code.Length;
                }

                var line = parsedCode.Code.AsMemory(lineStart, lineEnd - lineStart);
                var tokenized = grammar.TokenizeLine(line, ruleStack, TimeSpan.FromSeconds(1));
                ruleStack = tokenized.RuleStack;
                renderer.Write("<span class=\"rsr-code-line\">");
                WriteTokenizedLine(renderer, parsedCode, lineStart, line.Length, tokenized.Tokens);
                renderer.Write("</span>");

                if (!hasLineBreak)
                {
                    break;
                }

                lineStart = lineEnd + 1;
            }
        }
    }

    private static void WriteTokenizedLine(
        HtmlRenderer renderer,
        ParsedCode parsedCode,
        int lineStart,
        int lineLength,
        IToken[] tokens)
    {
        var styledRanges = new List<StyledRange>(tokens.Length + 1);
        var cursor = 0;
        foreach (var token in tokens)
        {
            var tokenStart = Math.Clamp(token.StartIndex, cursor, lineLength);
            var tokenEnd = Math.Clamp(token.EndIndex, tokenStart, lineLength);
            if (tokenStart > cursor)
            {
                styledRanges.Add(new StyledRange(
                    lineStart + cursor,
                    tokenStart - cursor,
                    null));
            }

            styledRanges.Add(new StyledRange(
                lineStart + tokenStart,
                tokenEnd - tokenStart,
                ResolveTokenStyle(token.Scopes)));
            cursor = tokenEnd;
        }

        if (cursor < lineLength)
        {
            styledRanges.Add(new StyledRange(
                lineStart + cursor,
                lineLength - cursor,
                null));
        }
        WriteCodeWithDiff(
            renderer,
            parsedCode,
            lineStart,
            lineLength,
            styledRanges);
    }

    private static TokenStyle? ResolveTokenStyle(IList<string> scopes)
    {
        var light = ResolveThemeStyle(_lightTheme, scopes);
        var dark = ResolveThemeStyle(_darkTheme, scopes);
        if (light.Foreground is null
            && dark.Foreground is null
            && light.FontStyle is FontStyle.None or FontStyle.NotSet
            && dark.FontStyle is FontStyle.None or FontStyle.NotSet)
        {
            return null;
        }

        return new TokenStyle(
            light.Foreground,
            dark.Foreground,
            NormalizeFontStyle(light.FontStyle) | NormalizeFontStyle(dark.FontStyle));
    }

    private static FontStyle NormalizeFontStyle(FontStyle fontStyle)
        => fontStyle == FontStyle.NotSet ? FontStyle.None : fontStyle;

    private static ThemeStyle ResolveThemeStyle(Theme theme, IList<string> scopes)
    {
        var foreground = 0;
        var fontStyle = FontStyle.NotSet;
        foreach (var rule in theme.Match(scopes))
        {
            if (foreground == 0 && rule.foreground > 0)
            {
                foreground = rule.foreground;
            }
            if (fontStyle == FontStyle.NotSet && rule.fontStyle != FontStyle.NotSet)
            {
                fontStyle = rule.fontStyle;
            }
        }

        return new ThemeStyle(
            foreground > 0 ? theme.GetColor(foreground) : null,
            fontStyle);
    }

    private static void WriteCodeWithDiff(
        HtmlRenderer renderer,
        ParsedCode parsedCode,
        int start,
        int length,
        IReadOnlyList<StyledRange> styledRanges)
    {
        if (length == 0)
        {
            WriteGapMarkers(renderer, parsedCode.DiffRanges, start);
            return;
        }

        var end = start + length;
        var boundaries = new SortedSet<int> { start, end };
        foreach (var range in parsedCode.DiffRanges)
        {
            if (range.Length == 0)
            {
                if (range.Start >= start && range.Start < end)
                {
                    boundaries.Add(range.Start);
                }
                continue;
            }

            var rangeEnd = range.Start + range.Length;
            if (range.Start < end && rangeEnd > start)
            {
                boundaries.Add(Math.Max(start, range.Start));
                boundaries.Add(Math.Min(end, rangeEnd));
            }
        }

        var boundaryList = boundaries.ToArray();
        for (var index = 0; index < boundaryList.Length - 1; index++)
        {
            var segmentStart = boundaryList[index];
            var segmentEnd = boundaryList[index + 1];
            WriteGapMarkers(renderer, parsedCode.DiffRanges, segmentStart);

            var diffRange = parsedCode.DiffRanges.FirstOrDefault(
                range => range.Length > 0
                    && range.Start <= segmentStart
                    && range.Start + range.Length >= segmentEnd);
            if (diffRange.Length > 0)
            {
                renderer.Write("<span class=\"");
                renderer.Write(diffRange.ClassName);
                renderer.Write("\">");
            }

            WriteStyledCode(
                renderer,
                parsedCode.Code,
                segmentStart,
                segmentEnd - segmentStart,
                styledRanges);
            if (diffRange.Length > 0)
            {
                renderer.Write("</span>");
            }
        }
        WriteGapMarkers(renderer, parsedCode.DiffRanges, end);
    }

    private static void WriteStyledCode(
        HtmlRenderer renderer,
        string code,
        int start,
        int length,
        IReadOnlyList<StyledRange> styledRanges)
    {
        var end = start + length;
        var cursor = start;
        foreach (var range in styledRanges)
        {
            var rangeEnd = range.Start + range.Length;
            if (rangeEnd <= start || range.Start >= end)
            {
                continue;
            }

            var segmentStart = Math.Max(start, range.Start);
            var segmentEnd = Math.Min(end, rangeEnd);
            if (segmentStart > cursor)
            {
                renderer.Write(WebUtility.HtmlEncode(code[cursor..segmentStart]));
            }
            WriteSyntaxSpan(
                renderer,
                code[segmentStart..segmentEnd],
                range.Style);
            cursor = segmentEnd;
        }

        if (cursor < end)
        {
            renderer.Write(WebUtility.HtmlEncode(code[cursor..end]));
        }
    }

    private static void WriteSyntaxSpan(
        HtmlRenderer renderer,
        string code,
        TokenStyle? style)
    {
        if (style is not { } tokenStyle)
        {
            renderer.Write(WebUtility.HtmlEncode(code));
            return;
        }

        renderer.Write("<span class=\"rsr-syntax-token\" style=\"");
        if (!string.IsNullOrWhiteSpace(tokenStyle.LightForeground))
        {
            renderer.Write("--rsr-syntax-light:");
            renderer.Write(WebUtility.HtmlEncode(tokenStyle.LightForeground));
            renderer.Write(';');
        }
        if (!string.IsNullOrWhiteSpace(tokenStyle.DarkForeground))
        {
            renderer.Write("--rsr-syntax-dark:");
            renderer.Write(WebUtility.HtmlEncode(tokenStyle.DarkForeground));
            renderer.Write(';');
        }
        AppendFontStyle(renderer, tokenStyle.FontStyle);
        renderer.Write("\">");
        renderer.Write(WebUtility.HtmlEncode(code));
        renderer.Write("</span>");
    }

    private static void AppendFontStyle(HtmlRenderer renderer, FontStyle fontStyle)
    {
        if ((fontStyle & FontStyle.Italic) != 0)
        {
            renderer.Write("font-style:italic;");
        }
        if ((fontStyle & FontStyle.Bold) != 0)
        {
            renderer.Write("font-weight:700;");
        }
        if ((fontStyle & FontStyle.Underline) != 0)
        {
            renderer.Write("text-decoration:underline;");
        }
    }

    private static void WritePlainCode(HtmlRenderer renderer, ParsedCode parsedCode)
    {
        var lineStart = 0;
        while (lineStart <= parsedCode.Code.Length)
        {
            var lineEnd = parsedCode.Code.IndexOf('\n', lineStart);
            var hasLineBreak = lineEnd >= 0;
            if (!hasLineBreak)
            {
                lineEnd = parsedCode.Code.Length;
            }

            var lineLength = lineEnd - lineStart;
            renderer.Write("<span class=\"rsr-code-line\">");
            WriteCodeWithDiff(
                renderer,
                parsedCode,
                lineStart,
                lineLength,
                [new StyledRange(lineStart, lineLength, null)]);
            renderer.Write("</span>");

            if (!hasLineBreak)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }
    }

    private static void WriteGapMarkers(
        HtmlRenderer renderer,
        IReadOnlyList<DiffRange> ranges,
        int position)
    {
        foreach (var range in ranges)
        {
            if (range.Length != 0 || range.Start != position)
            {
                continue;
            }

            renderer.Write("<span class=\"");
            renderer.Write(range.ClassName);
            renderer.Write("\" aria-hidden=\"true\"></span>");
        }
    }

    [GeneratedRegex(
        """\u001eRSR-CODE-DIFF:(?<id>[0-9a-f]{32}):(?:(?<gap>gap:(?<gapClass>rsr-rendered-diff-(?:added|removed) rsr-rendered-diff-gap))|(?<open>open:(?<openClass>rsr-rendered-diff-(?:added|removed)))|(?<close>close))\u001f""",
        RegexOptions.CultureInvariant)]
    private static partial Regex RenderedDiffMarkerRegex();

    private readonly record struct ParsedCode(string Code, IReadOnlyList<DiffRange> DiffRanges);

    private readonly record struct DiffRange(int Start, int Length, string ClassName);

    private readonly record struct OpenDiffMarker(int Start, string ClassName);

    private readonly record struct ThemeStyle(string? Foreground, FontStyle FontStyle);

    private readonly record struct TokenStyle(
        string? LightForeground,
        string? DarkForeground,
        FontStyle FontStyle);

    private readonly record struct StyledRange(
        int Start,
        int Length,
        TokenStyle? Style);
}
