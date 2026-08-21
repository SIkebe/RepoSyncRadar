using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Renders a single Markdown document to a self-contained HTML page used by
/// the Markdown-first preview path (IMPLEMENTATION_PLAN.md §Step 19.7 / 19.8).
/// Instead of compiling the whole github/docs site, it reads one file from the
/// bare clone materialization and feeds it to Markdig directly. Frontmatter is
/// stripped (title / intro promoted to a
/// header), and Liquid tags such as <c>{% data variables.x %}</c> are first
/// evaluated by <see cref="DocsLiquidEvaluator"/> using
/// <see cref="DocsLiquidContext"/> read from the worktree; anything that
/// remains unresolved is wrapped in a grey placeholder span so the raw
/// template syntax never leaks into the rendered body.
/// </summary>
internal static partial class MarkdownPreviewRenderer
{
    public enum RenderedMarkdownDiffSide
    {
        None,
        Before,
        After,
    }

    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseRepoSyncRadarSyntaxHighlighting()
        // NOTE: We intentionally do NOT call DisableHtml() here. github/docs
        // markdown sources ship with inline HTML (<picture>, <video>, tables
        // with <thead>, <details>/<summary>, etc.) that are integral to the
        // rendered article; disabling HTML would leak the literal tags as
        // text. The same applies to the <span class="rsr-liquid"> markers
        // NeutralizeLiquid injects for unresolved Liquid tags — with HTML
        // disabled they showed up as raw "<span class=…>" strings in the
        // preview (the visual regression that motivated Step 19.8).
        .Build();

    private static readonly HashSet<string> _similarityStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "after",
        "before",
        "being",
        "class",
        "could",
        "either",
        "following",
        "href",
        "should",
        "span",
        "their",
        "there",
        "these",
        "those",
        "through",
        "variables",
        "which",
        "while",
        "would",
        "your",
    };

    // Liquid block / variable syntax used pervasively in github/docs content.
    // We never attempt to evaluate these — only to neutralise them so Markdig
    // does not render literal `{% ifversion fpt %}` into a <p> tag and the
    // reviewer can still see which tag was present at that position.
    [GeneratedRegex(@"\{%-?\s*(.*?)\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex LiquidBlockRegex();

    [GeneratedRegex(@"\{\{-?\s*(.*?)\s*-?\}\}", RegexOptions.Singleline)]
    private static partial Regex LiquidVariableRegex();

    [GeneratedRegex(@"\{%-?\s*(?<tag>note|tip|warning|danger)\s*-?%\}(?<body>.*?)\{%-?\s*end\k<tag>\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex SpotlightBlockRegex();

    [GeneratedRegex(@"\{%-?\s*(?<tag>vscode|jetbrains|visualstudio|cli|webui|eclipse|desktop|vimneovim|azure_data_studio|xcode|curl|javascript|windowsterminal|codespaces|api|mobile|copilotcli|bash|powershell|skillsets|agents|jetbrains_beta|github_mobile|ides|importer_cli|mac|windows|linux|rowheaders)\s*-?%\}(?<body>.*?)\{%-?\s*end\k<tag>\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex ToolBlockRegex();

    [GeneratedRegex(@"\{%-?\s*prompt\s*-?%\}(?<body>.*?)\{%-?\s*endprompt\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex PromptBlockRegex();

    [GeneratedRegex(@"\{%-?\s*codetabs\s*-?%\}(?<body>.*?)\{%-?\s*endcodetabs\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex CodeTabsBlockRegex();

    [GeneratedRegex(@"\{%-?\s*codetab\s+(?<label>[^%]*?)\s*-?%\}(?<body>.*?)\{%-?\s*endcodetab\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex CodeTabBlockRegex();

    [GeneratedRegex("""<a\b(?<attrs>[^>]*)>(?<body>.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex("""\bhref\s*=\s*(?<quote>["'])(?<href>.*?)\k<quote>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorHrefRegex();

    [GeneratedRegex("""<span\b(?<attrs>[^>]*)>\s*AUTOTITLE\s*</span>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AutotitleSpanRegex();

    [GeneratedRegex(@"\[AUTOTITLE\]\((?<href>[^\s)]+)\)?", RegexOptions.IgnoreCase)]
    private static partial Regex AutotitleMarkdownLinkRegex();

    [GeneratedRegex("""\[(?<label><span\b(?<attrs>[^>]*)>\s*AUTOTITLE\s*</span>|AUTOTITLE)\]\((?<destination><[^>\r\n]+>|[^\s)]+)(?<suffix>(?:\s+(?:"[^"]*"|'[^']*'|\([^)]*\)))?\))""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FullAutotitleMarkdownLinkRegex();

    [GeneratedRegex("""<[^>]+>""", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("""<!--.*?-->""", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex("""&lt;span class=&quot;(?<class>rsr-rendered-diff-(?:added|removed)(?:\s+rsr-rendered-diff-gap)?)&quot;.*?&gt;(?<body>.*?)&lt;/span&gt;""", RegexOptions.Singleline)]
    private static partial Regex EscapedRenderedDiffMarkerRegex();

    [GeneratedRegex("""[a-zA-Z_][a-zA-Z0-9_-]*""")]
    private static partial Regex VersionExpressionIdentifierRegex();

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SimilarityTokenRegex();

    private const string _copilotOcticonSvg = """
        <svg version="1.1" width="16" height="16" viewBox="0 0 16 16" class="octicon octicon-copilot" aria-hidden="true" data-component="Octicon"><path d="M7.998 15.035c-4.562 0-7.873-2.914-7.998-3.749V9.338c.085-.628.677-1.686 1.588-2.065.013-.07.024-.143.036-.218.029-.183.06-.384.126-.612-.201-.508-.254-1.084-.254-1.656 0-.87.128-1.769.693-2.484.579-.733 1.494-1.124 2.724-1.261 1.206-.134 2.262.034 2.944.765.05.053.096.108.139.165.044-.057.094-.112.143-.165.682-.731 1.738-.899 2.944-.765 1.23.137 2.145.528 2.724 1.261.566.715.693 1.614.693 2.484 0 .572-.053 1.148-.254 1.656.066.228.098.429.126.612.012.076.024.148.037.218.924.385 1.522 1.471 1.591 2.095v1.872c0 .766-3.351 3.795-8.002 3.795Zm0-1.485c2.28 0 4.584-1.11 5.002-1.433V7.862l-.023-.116c-.49.21-1.075.291-1.727.291-1.146 0-2.059-.327-2.71-.991A3.222 3.222 0 0 1 8 6.303a3.24 3.24 0 0 1-.544.743c-.65.664-1.563.991-2.71.991-.652 0-1.236-.081-1.727-.291l-.023.116v4.255c.419.323 2.722 1.433 5.002 1.433ZM6.762 2.83c-.193-.206-.637-.413-1.682-.297-1.019.113-1.479.404-1.713.7-.247.312-.369.789-.369 1.554 0 .793.129 1.171.308 1.371.162.181.519.379 1.442.379.853 0 1.339-.235 1.638-.54.315-.322.527-.827.617-1.553.117-.935-.037-1.395-.241-1.614Zm4.155-.297c-1.044-.116-1.488.091-1.681.297-.204.219-.359.679-.242 1.614.091.726.303 1.231.618 1.553.299.305.784.54 1.638.54.922 0 1.28-.198 1.442-.379.179-.2.308-.578.308-1.371 0-.765-.123-1.242-.37-1.554-.233-.296-.693-.587-1.713-.7Z"></path><path d="M6.25 9.037a.75.75 0 0 1 .75.75v1.501a.75.75 0 0 1-1.5 0V9.787a.75.75 0 0 1 .75-.75Zm4.25.75v1.501a.75.75 0 0 1-1.5 0V9.787a.75.75 0 0 1 1.5 0Z"></path></svg>
        """;

    public static string RenderDocument(
        string repoPath,
        string? markdown,
        string sha,
        string label,
        DocsLiquidContext? liquidContext = null,
        DocsVersion? version = null,
        IReadOnlyList<DocsVersion>? affectedVersions = null,
        DocsVersion? selectedVersion = null,
        IReadOnlyList<DocsVersionImpactDetail>? versionImpacts = null,
        IReadOnlyList<MarkdownFrontmatterChange>? frontmatterChanges = null,
        MarkdownSourceDiffSummary? sourceDiff = null,
        string? assetBasePath = null,
        string? diffAgainstMarkdown = null,
        DocsLiquidContext? diffAgainstLiquidContext = null,
        string? diffAgainstRepoPath = null,
        RenderedMarkdownDiffSide diffSide = RenderedMarkdownDiffSide.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var effectiveVersion = version ?? DocsVersionCatalog.Default;
        var effectiveLiquidContext = liquidContext ?? DocsLiquidContext.Empty;
        var trimmedRepoPath = repoPath.Trim();
        var repoPathDisplay = WebUtility.HtmlEncode(trimmedRepoPath);
        var meta = WebUtility.HtmlEncode($"{label} {ShortSha(sha)}");
        string titleText;
        string titleHtml;
        string? introHtml;
        var showRepoPath = false;
        var hasVisibleBody = false;
        string body;

        if (markdown is null)
        {
            titleText = trimmedRepoPath;
            titleHtml = repoPathDisplay;
            introHtml = null;
            body = "<p class=\"rsr-empty\">この時点にはファイルがありません。</p>";
        }
        else
        {
            var (frontmatter, content) = SplitFrontmatter(markdown);
            var frontmatterTitle = ExtractFrontmatterScalar(frontmatter, "title");
            var frontmatterIntro = ExtractFrontmatterScalar(frontmatter, "intro");
            var displayTitle = frontmatterTitle ?? trimmedRepoPath;
            titleText = EvaluateLiquidText(displayTitle, effectiveLiquidContext, effectiveVersion);
            titleHtml = RenderInlineWithLiquid(displayTitle, effectiveLiquidContext, effectiveVersion);
            introHtml = frontmatterIntro is null
                ? null
                : RenderInlineWithLiquid(frontmatterIntro, effectiveLiquidContext, effectiveVersion);
            showRepoPath = frontmatterTitle is not null
                && !string.Equals(displayTitle, trimmedRepoPath, StringComparison.Ordinal);
            // First expand Liquid tags whose definitions we found in the
            // worktree (variables / reusables / ifversion per `effectiveVersion`);
            // any tag left behind is then wrapped in <span class="rsr-liquid"> by
            // NeutralizeLiquid so the reviewer still sees its original syntax.
            var liquidEvaluated = DocsLiquidEvaluator.Evaluate(
                content,
                effectiveLiquidContext,
                effectiveVersion,
                comparisonContext: diffAgainstLiquidContext);
            var tableFragmentsExpanded = ExpandMarkdownTableFragments(liquidEvaluated);
            var compareAutotitleLabels = diffAgainstLiquidContext is not null;
            var renderedDiffInput = compareAutotitleLabels
                ? RewriteAutotitleMarkdownLinks(
                    tableFragmentsExpanded,
                    trimmedRepoPath,
                    effectiveLiquidContext,
                    effectiveVersion)
                : tableFragmentsExpanded;
            var diffMarked = ApplyRenderedMarkdownDiff(
                renderedDiffInput,
                diffAgainstMarkdown,
                diffAgainstLiquidContext ?? DocsLiquidContext.Empty,
                trimmedRepoPath,
                diffAgainstRepoPath?.Trim() ?? trimmedRepoPath,
                effectiveLiquidContext,
                effectiveVersion,
                compareAutotitleLabels,
                diffSide);
            var protectedHtmlFragments = new RenderedHtmlPlaceholderStore();
            var liquidBlocksRendered = RenderOfficialLiquidBlocks(diffMarked, protectedHtmlFragments);
            var githubAlertsRendered = RenderGitHubAlertBlocks(liquidBlocksRendered);
            var autotitleMarkdownRewritten = compareAutotitleLabels
                ? githubAlertsRendered
                : RewriteAutotitleMarkdownLinks(
                    githubAlertsRendered,
                    trimmedRepoPath,
                    effectiveLiquidContext,
                    effectiveVersion);
            var liquidNeutralized = NeutralizeLiquid(autotitleMarkdownRewritten);
            body = Markdown.ToHtml(liquidNeutralized, _pipeline);
            body = protectedHtmlFragments.Restore(body);
            body = RestoreEscapedRenderedDiffMarkers(body);
            hasVisibleBody = HasVisibleBodyMarkup(body);
            if (!hasVisibleBody)
            {
                body = frontmatterTitle is null && frontmatterIntro is null
                    ? "<p class=\"rsr-empty\">空の Markdown ファイルです。</p>"
                    : "<p class=\"rsr-empty\">このファイルは存在しますが、本文はありません。フロントマターのみ、または自動生成コメントのみの Markdown です。</p>";
            }
            body = RewriteAutotitleLinks(body, trimmedRepoPath, effectiveLiquidContext, effectiveVersion);
            body = RewriteAssetReferences(body, trimmedRepoPath, assetBasePath);
        }

        var html = new StringBuilder(capacity: body.Length + 2200);
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"ja\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(WebUtility.HtmlEncode(titleText)).AppendLine("</title>");
        html.AppendLine("<style>");
        // Light palette (default). Both the WPF theme toggle
        // (MainWindow.xaml.cs `BuildDocsThemeScript`) and the OS
        // `prefers-color-scheme` are mapped onto these CSS variables so a
        // single rule set drives the live colours.
        html.AppendLine(":root{--rsr-bg:#f6f8fa;--rsr-fg:#24292f;--rsr-article-bg:#fff;--rsr-border:#d8dee4;--rsr-muted:#57606a;--rsr-link:#0969da;--rsr-code-bg:#afb8c133;--rsr-pre-bg:#f6f8fa;--rsr-blockquote-border:#d0d7de;--rsr-th-bg:#f6f8fa;--rsr-liquid-bg:#fff8c5;--rsr-liquid-fg:#7d4e00;--rsr-liquid-border:#d4a72c;color-scheme:light;}");
        // OS-level dark preference, but ONLY when the user has not pinned
        // light via the toggle (`data-color-mode="light"`). This keeps the
        // explicit user choice authoritative over the OS hint.
        html.AppendLine("@media (prefers-color-scheme: dark){:root:not([data-color-mode=\"light\"]){--rsr-bg:#0d1117;--rsr-fg:#c9d1d9;--rsr-article-bg:#0d1117;--rsr-border:#30363d;--rsr-muted:#8b949e;--rsr-link:#58a6ff;--rsr-code-bg:#6e768166;--rsr-pre-bg:#161b22;--rsr-blockquote-border:#30363d;--rsr-th-bg:#161b22;--rsr-liquid-bg:#3c2e00;--rsr-liquid-fg:#e3b341;--rsr-liquid-border:#9e6a03;color-scheme:dark;}}");
        // Explicit user selection from the toggle always wins, regardless of
        // OS preference.
        html.AppendLine(":root[data-color-mode=\"dark\"]{--rsr-bg:#0d1117;--rsr-fg:#c9d1d9;--rsr-article-bg:#0d1117;--rsr-border:#30363d;--rsr-muted:#8b949e;--rsr-link:#58a6ff;--rsr-code-bg:#6e768166;--rsr-pre-bg:#161b22;--rsr-blockquote-border:#30363d;--rsr-th-bg:#161b22;--rsr-liquid-bg:#3c2e00;--rsr-liquid-fg:#e3b341;--rsr-liquid-border:#9e6a03;color-scheme:dark;}");
        html.AppendLine(":root[data-color-mode=\"light\"]{--rsr-bg:#f6f8fa;--rsr-fg:#24292f;--rsr-article-bg:#fff;--rsr-border:#d8dee4;--rsr-muted:#57606a;--rsr-link:#0969da;--rsr-code-bg:#afb8c133;--rsr-pre-bg:#f6f8fa;--rsr-blockquote-border:#d0d7de;--rsr-th-bg:#f6f8fa;--rsr-liquid-bg:#fff8c5;--rsr-liquid-fg:#7d4e00;--rsr-liquid-border:#d4a72c;color-scheme:light;}");
        html.AppendLine("body{margin:0;background:var(--rsr-bg);color:var(--rsr-fg);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;line-height:1.55;}");
        html.AppendLine("main{max-width:920px;margin:0 auto;padding:32px 24px 64px;}");
        html.AppendLine("article{background:var(--rsr-article-bg);border:1px solid var(--rsr-border);border-radius:6px;padding:28px;}");
        html.AppendLine("header{border-bottom:1px solid var(--rsr-border);margin-bottom:24px;padding-bottom:16px;}");
        html.AppendLine("h1,h2,h3,h4,h5,h6{line-height:1.25;margin:1.25em 0 .55em;font-weight:650;}");
        html.AppendLine("header h1{font-size:1.55rem;margin:0 0 6px;}");
        html.AppendLine(".rsr-meta{color:var(--rsr-muted);font-size:.85rem;margin:0;}");
        html.AppendLine(".rsr-path{color:var(--rsr-muted);font-size:.78rem;margin:4px 0 0;font-family:'Cascadia Mono',Consolas,monospace;}");
        html.AppendLine(".rsr-intro{color:var(--rsr-fg);font-size:1.05rem;margin:0 0 1.25rem;font-weight:500;}");
        html.AppendLine("p,ul,ol,pre,blockquote,table{margin:0 0 1rem;}");
        html.AppendLine("a{color:var(--rsr-link);}code{background:var(--rsr-code-bg);border-radius:4px;padding:.12em .28em;font-family:'Cascadia Mono',Consolas,monospace;font-size:.92em;}");
        html.AppendLine("pre{background:var(--rsr-pre-bg);border-radius:6px;overflow:auto;padding:16px;}pre code{background:transparent;padding:0;}");
        html.AppendLine(".rsr-code-line{display:block;min-height:1.55em;}.rsr-syntax-token{color:var(--rsr-syntax-light);}");
        html.AppendLine("@media (prefers-color-scheme: dark){:root:not([data-color-mode=\"light\"]) .rsr-syntax-token{color:var(--rsr-syntax-dark);}}");
        html.AppendLine(":root[data-color-mode=\"dark\"] .rsr-syntax-token{color:var(--rsr-syntax-dark);}:root[data-color-mode=\"light\"] .rsr-syntax-token{color:var(--rsr-syntax-light);}");
        // Code blocks normally scroll horizontally to mirror docs.github.com, but a
        // changed token can then sit past the right edge of the narrow comparison
        // pane, so navigating to its scrollbar marker shows no visible diff. Wrap
        // only the code blocks that actually contain a diff so the highlighted
        // change is always on screen; untouched code keeps the docs scroll behavior.
        html.AppendLine("pre:has(.rsr-rendered-diff-added,.rsr-rendered-diff-removed){white-space:pre-wrap;overflow-wrap:anywhere;}pre:has(.rsr-rendered-diff-added,.rsr-rendered-diff-removed) code{white-space:inherit;overflow-wrap:inherit;}");
        html.AppendLine("img,video{max-width:100%;height:auto;}picture{display:block;margin:0 0 1rem;}picture img{margin-bottom:0;}");
        html.AppendLine("blockquote{border-left:4px solid var(--rsr-blockquote-border);color:var(--rsr-muted);padding-left:1rem;}table{border-collapse:collapse;display:block;overflow:auto;}td,th{border:1px solid var(--rsr-border);padding:6px 13px;}th{background:var(--rsr-th-bg);}");
        html.AppendLine(".rsr-rendered-diff-added{background:#2da44e24;border-radius:3px;box-shadow:0 0 0 2px #2da44e24;}.rsr-rendered-diff-removed{background:#cf222e24;border-radius:3px;box-shadow:0 0 0 2px #cf222e24;text-decoration-line:line-through;text-decoration-color:rgba(207,34,46,.85);text-decoration-thickness:1.2px;text-decoration-skip-ink:none;}.rsr-rendered-diff-gap{display:inline-block;width:.55em;height:1.05em;margin:0 .12em;vertical-align:-.15em;text-decoration:none;}");
        html.AppendLine("td .rsr-rendered-diff-added,th .rsr-rendered-diff-added,td .rsr-rendered-diff-removed,th .rsr-rendered-diff-removed{display:block;margin:-6px -13px;padding:6px 13px;}");
        html.AppendLine(".rsr-diff-scrollbar{bottom:0;pointer-events:none;position:fixed;right:0;top:0;width:10px;z-index:2147483647;}.rsr-diff-scrollbar-marker{border-radius:999px;box-shadow:0 0 0 1px rgba(255,255,255,.7),0 1px 3px rgba(0,0,0,.25);min-height:4px;position:absolute;right:0;width:10px;}.rsr-diff-scrollbar-marker--added{background:#2da44e;}.rsr-diff-scrollbar-marker--removed{background:#cf222e;}");
        html.AppendLine(".octicon{display:inline-block;vertical-align:text-bottom;fill:currentColor;overflow:visible;}");
        html.AppendLine(".ghd-alert{border:1px solid var(--rsr-border);border-left-width:4px;border-radius:6px;margin:0 0 1rem;padding:12px 14px;background:var(--rsr-article-bg);}");
        html.AppendLine(".ghd-alert>:last-child,.ghd-tool>:last-child{margin-bottom:0;}");
        html.AppendLine(".ghd-alert-accent{border-left-color:#0969da;}.ghd-alert-success{border-left-color:#1a7f37;}.ghd-alert-attention{border-left-color:#9a6700;}.ghd-alert-danger{border-left-color:#cf222e;}");
        html.AppendLine(".ghd-markdown-alert{border:0;border-left:4px solid var(--rsr-alert-color);border-radius:0;margin:0 0 1rem;padding:8px 0 8px 14px;background:transparent;color:var(--rsr-fg);}");
        html.AppendLine(".ghd-markdown-alert>:last-child{margin-bottom:0;}.ghd-markdown-alert-title{align-items:center;color:var(--rsr-alert-color);display:flex;font-weight:650;gap:6px;margin:0 0 8px;}");
        html.AppendLine(".ghd-markdown-alert-note{--rsr-alert-color:#0969da;}.ghd-markdown-alert-tip{--rsr-alert-color:#1a7f37;}.ghd-markdown-alert-important{--rsr-alert-color:#8250df;}.ghd-markdown-alert-warning{--rsr-alert-color:#9a6700;}.ghd-markdown-alert-caution{--rsr-alert-color:#cf222e;}");
        html.AppendLine(".ghd-tool{border:1px solid var(--rsr-border);border-left:4px solid var(--rsr-link);border-radius:6px;margin:0 0 1rem;padding:12px 14px;background:var(--rsr-pre-bg);}");
        html.AppendLine(".ghd-code-tabs{border:1px solid var(--rsr-border);border-radius:6px;margin:0 0 1rem;background:var(--rsr-article-bg);overflow:hidden;}.ghd-code-tab+.ghd-code-tab{border-top:1px solid var(--rsr-border);}.ghd-code-tab-label{background:var(--rsr-th-bg);border-bottom:1px solid var(--rsr-border);color:var(--rsr-muted);font:600 .78rem 'Cascadia Mono',Consolas,monospace;padding:6px 10px;text-transform:none;}.ghd-code-tab-body{padding:10px;}.ghd-code-tab-body>:last-child{margin-bottom:0;}.ghd-code-tab-body pre{margin:0;}");
        html.AppendLine(".copilot-prompt-long,.copilot-prompt-short{display:inline-flex;align-items:center;color:var(--rsr-link);margin-left:.25rem;text-decoration:none;}.copilot-prompt-short{display:none;}");
        html.AppendLine(".rsr-liquid{display:inline-block;background:var(--rsr-liquid-bg);color:var(--rsr-liquid-fg);border:1px solid var(--rsr-liquid-border);border-radius:3px;padding:0 .35em;margin:0 .15em;font-size:.82em;font-family:'Cascadia Mono',Consolas,monospace;}");
        html.AppendLine(".rsr-empty{color:var(--rsr-muted);font-style:italic;}");
        html.AppendLine(".rsr-version-bar{margin:10px 0 0;display:flex;flex-wrap:wrap;gap:8px;align-items:center;font-size:.82rem;}");
        html.AppendLine(".rsr-version-current{color:var(--rsr-muted);}");
        html.AppendLine(".rsr-version-impact-label{color:var(--rsr-muted);}");
        html.AppendLine(".rsr-version-badges{display:inline-flex;flex-wrap:wrap;gap:6px;padding:0;margin:0;list-style:none;}");
        html.AppendLine(".rsr-version-badge{display:inline-block;padding:2px 8px;background:var(--rsr-th-bg);border:1px solid var(--rsr-border);border-radius:12px;font:inherit;font-size:.76rem;color:var(--rsr-fg);cursor:pointer;}");
        html.AppendLine(".rsr-version-badge:hover{border-color:var(--rsr-link);color:var(--rsr-link);}");
        html.AppendLine(".rsr-version-badge:focus-visible{outline:2px solid var(--rsr-link);outline-offset:2px;}");
        html.AppendLine(".rsr-version-badge--current{background:var(--rsr-liquid-bg);color:var(--rsr-liquid-fg);border-color:var(--rsr-liquid-border);font-weight:600;}");
        html.AppendLine(".rsr-version-badge--current:hover{color:var(--rsr-liquid-fg);border-color:var(--rsr-liquid-border);cursor:default;}");
        html.AppendLine(".rsr-version-empty{color:var(--rsr-muted);font-style:italic;}");
        html.AppendLine(".rsr-source-diff{margin:14px 0 0;border:1px solid var(--rsr-border);border-left:4px solid #bf8700;border-radius:6px;background:var(--rsr-pre-bg);padding:12px;}");
        html.AppendLine(".rsr-source-diff h2{font-size:.92rem;margin:0 0 6px;}");
        html.AppendLine(".rsr-source-diff-overview{color:var(--rsr-muted);font-size:.78rem;margin:0 0 10px;}");
        html.AppendLine(".rsr-source-diff-list{display:grid;gap:8px;list-style:none;margin:0;padding:0;}");
        html.AppendLine(".rsr-source-change{border:1px solid var(--rsr-border);border-radius:6px;background:var(--rsr-article-bg);padding:8px;}");
        html.AppendLine(".rsr-source-change[data-change-kind='added']{border-left:3px solid #2da44e;}");
        html.AppendLine(".rsr-source-change[data-change-kind='removed']{border-left:3px solid #cf222e;}");
        html.AppendLine(".rsr-source-change[data-change-kind='updated']{border-left:3px solid #bf8700;}");
        html.AppendLine(".rsr-source-change-kind{color:var(--rsr-muted);display:block;font-size:.72rem;font-weight:700;margin-bottom:4px;}");
        html.AppendLine(".rsr-source-line{display:grid;grid-template-columns:5.5rem minmax(0,1fr);gap:8px;margin:3px 0;font-size:.8rem;}");
        html.AppendLine(".rsr-source-line-label{color:var(--rsr-muted);font-weight:700;}");
        html.AppendLine(".rsr-source-line code{display:block;white-space:pre-wrap;overflow-wrap:anywhere;}");
        html.AppendLine(".rsr-source-file{color:var(--rsr-muted);font-family:'Cascadia Mono',Consolas,monospace;font-size:.76rem;margin:8px 0 4px;}");
        html.AppendLine(".rsr-version-diff-summary{margin:14px 0 0;border:1px solid var(--rsr-border);border-radius:6px;background:var(--rsr-pre-bg);padding:12px;}");
        html.AppendLine(".rsr-version-diff-summary h2{font-size:.92rem;margin:0 0 6px;}");
        html.AppendLine(".rsr-version-diff-overview{color:var(--rsr-muted);font-size:.78rem;margin:0 0 10px;}");
        html.AppendLine(".rsr-version-diff-list{display:grid;gap:8px;list-style:none;margin:0;padding:0;}");
        html.AppendLine(".rsr-version-diff-item{border:1px solid var(--rsr-border);border-radius:6px;background:var(--rsr-article-bg);padding:10px;}");
        html.AppendLine(".rsr-version-diff-title{align-items:center;display:flex;flex-wrap:wrap;gap:6px;margin:0 0 6px;font-size:.84rem;}");
        html.AppendLine(".rsr-version-pattern-versions{display:flex;flex-wrap:wrap;gap:5px;list-style:none;margin:0 0 8px;padding:0;}");
        html.AppendLine(".rsr-version-pattern-badge{display:inline-block;padding:2px 7px;background:var(--rsr-th-bg);border:1px solid var(--rsr-border);border-radius:12px;font:inherit;font-size:.72rem;color:var(--rsr-fg);cursor:pointer;}");
        html.AppendLine(".rsr-version-pattern-badge:hover{border-color:var(--rsr-link);color:var(--rsr-link);}");
        html.AppendLine(".rsr-version-change{border-left:3px solid var(--rsr-border);display:grid;gap:4px;margin-top:8px;padding-left:8px;}");
        html.AppendLine(".rsr-version-change[data-change-kind='added']{border-left-color:#2da44e;}");
        html.AppendLine(".rsr-version-change[data-change-kind='removed']{border-left-color:#cf222e;}");
        html.AppendLine(".rsr-version-change[data-change-kind='updated']{border-left-color:#bf8700;}");
        html.AppendLine(".rsr-version-change-kind{color:var(--rsr-muted);font-size:.72rem;font-weight:700;}");
        html.AppendLine(".rsr-version-change-line{align-items:start;display:grid;grid-template-columns:4.5rem minmax(0,1fr);gap:8px;margin:0;font-size:.78rem;}");
        html.AppendLine(".rsr-version-change-label{color:var(--rsr-muted);font-weight:700;margin-right:.35rem;}");
        html.AppendLine(".rsr-version-change-excerpt{overflow-wrap:anywhere;}.rsr-version-change-excerpt--removed,.rsr-version-change-excerpt--added{border-radius:3px;padding:1px 3px;}.rsr-version-change-excerpt--removed{background:#cf222e24;box-shadow:0 0 0 2px #cf222e24;text-decoration-line:line-through;text-decoration-color:rgba(207,34,46,.85);text-decoration-thickness:1.2px;text-decoration-skip-ink:none;}.rsr-version-change-excerpt--added{background:#2da44e24;box-shadow:0 0 0 2px #2da44e24;}");
        html.AppendLine(".rsr-version-change-note{color:var(--rsr-muted);font-size:.78rem;margin:0;}");
        html.AppendLine(".rsr-version-diff-more{color:var(--rsr-muted);font-size:.76rem;margin:8px 0 0;}");
        html.AppendLine(".rsr-frontmatter-diff{border:1px solid var(--rsr-border);border-left:4px solid var(--rsr-link);border-radius:6px;background:var(--rsr-pre-bg);margin:0 0 1rem;padding:12px;}");
        html.AppendLine(".rsr-frontmatter-diff h2{font-size:.95rem;margin:0 0 6px;}");
        html.AppendLine(".rsr-frontmatter-diff-overview{color:var(--rsr-muted);font-size:.8rem;margin:0 0 10px;}");
        html.AppendLine(".rsr-frontmatter-diff-list{display:grid;gap:8px;list-style:none;margin:0;padding:0;}");
        html.AppendLine(".rsr-frontmatter-change{border:1px solid var(--rsr-border);border-radius:6px;background:var(--rsr-article-bg);padding:8px;}");
        html.AppendLine(".rsr-frontmatter-change[data-change-kind='added']{border-left:3px solid #2da44e;}");
        html.AppendLine(".rsr-frontmatter-change[data-change-kind='removed']{border-left:3px solid #cf222e;}");
        html.AppendLine(".rsr-frontmatter-change[data-change-kind='updated']{border-left:3px solid #bf8700;}");
        html.AppendLine(".rsr-frontmatter-change-kind{color:var(--rsr-muted);display:block;font-size:.72rem;font-weight:700;margin-bottom:4px;}");
        html.AppendLine(".rsr-frontmatter-line{display:grid;grid-template-columns:5.5rem minmax(0,1fr);gap:8px;margin:3px 0;font-size:.8rem;}");
        html.AppendLine(".rsr-frontmatter-line-label{color:var(--rsr-muted);font-weight:700;}");
        html.AppendLine(".rsr-frontmatter-line code{display:block;white-space:pre-wrap;overflow-wrap:anywhere;}");
        html.AppendLine("</style>");
        html.AppendLine("<script>");
        html.AppendLine("(() => { document.addEventListener('click', event => { const button = event.target?.closest?.('[data-rsr-version-slug]'); if (!button || button.getAttribute('aria-current') === 'true') return; const slug = button.getAttribute('data-rsr-version-slug'); if (!slug) return; window.chrome?.webview?.postMessage(`rsr-preview-version:${slug}`); }); })();");
        AppendDiffScrollbarScript(html);
        html.AppendLine("</script>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<main>");
        html.AppendLine("<article data-testid=\"article-body\">");
        html.AppendLine("<header>");
        html.Append("<h1>").Append(titleHtml).AppendLine("</h1>");
        html.Append("<p class=\"rsr-meta\">").Append(meta).AppendLine("</p>");
        if (showRepoPath)
        {
            // Surface the source repo path when the frontmatter title differs
            // from it, so reviewers can still match the rendered page back to
            // the file they clicked on.
            html.Append("<p class=\"rsr-path\">").Append(repoPathDisplay).AppendLine("</p>");
        }
        AppendVersionBadgeMarkup(html, selectedVersion ?? effectiveVersion, affectedVersions);
        AppendVersionDiffSummary(
            html,
            selectedVersion ?? effectiveVersion,
            versionImpacts,
            trimmedRepoPath,
            effectiveLiquidContext);
        AppendSourceDiffSummary(
            html,
            sourceDiff,
            trimmedRepoPath,
            effectiveLiquidContext,
            selectedVersion ?? effectiveVersion);
        html.AppendLine("</header>");
        if (!string.IsNullOrWhiteSpace(introHtml))
        {
            html.Append("<p class=\"rsr-intro\">")
            .Append(introHtml)
                .AppendLine("</p>");
        }
        if (markdown is not null)
        {
            html.Append(RenderFrontmatterDiff(frontmatterChanges, hasVisibleBody));
        }
        html.AppendLine(body);
        html.AppendLine("</article>");
        html.AppendLine("</main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static void AppendDiffScrollbarScript(StringBuilder html)
    {
        html.AppendLine("""
(() => {
    const stateKey = '__repoSyncRadarDiffScrollbar';
    const markerRootId = 'rsr-diff-scrollbar';
    const selector = '.rsr-rendered-diff-added,.rsr-rendered-diff-removed';
    const structuralBlockSelector = 'pre,li,td,th,blockquote,.ghd-markdown-alert';
    const textBlockSelector = 'p,h1,h2,h3,h4,h5,h6';
    const isRemoved = (element) =>
        element.matches('.rsr-rendered-diff-removed') ||
        element.querySelector('.rsr-rendered-diff-removed') !== null;
    const collectTargets = () => {
        const annotated = Array.from(
            document.querySelectorAll('[data-rsr-diff-navigation-index]'));
        if (annotated.length > 0) {
            const groups = new Map();
            annotated.forEach(element => {
                const navigationIndex = element.getAttribute('data-rsr-diff-navigation-index');
                if (!groups.has(navigationIndex)) {
                    groups.set(navigationIndex, { elements: [], removed: false });
                }
                const group = groups.get(navigationIndex);
                group.elements.push(element);
                group.removed = group.removed || isRemoved(element);
            });
            return Array.from(groups.values());
        }

        const seen = new Set();
        const targets = [];
        Array.from(document.querySelectorAll(selector)).forEach(element => {
            const target =
                element.closest(structuralBlockSelector) ||
                element.closest(textBlockSelector) ||
                element;
            if (seen.has(target)) return;
            seen.add(target);
            targets.push({ elements: [target], removed: isRemoved(target) });
        });
        return targets;
    };
    let pairs = [];
    const position = () => {
        if (pairs.length === 0) return;
        const docHeight = Math.max(1, document.documentElement.scrollHeight);
        const viewport = Math.max(1, window.innerHeight || document.documentElement.clientHeight || 1);
        const scrollbarSize = Math.max(0, window.innerWidth - document.documentElement.clientWidth);
        const buttonSize = Math.min(scrollbarSize, viewport / 4);
        const trackTop = buttonSize;
        const trackHeight = Math.max(1, viewport - buttonSize * 2);
        const scrollY = window.scrollY || window.pageYOffset || 0;
        pairs.forEach(pair => {
            const rects = pair.elements
                .map(element => element.getBoundingClientRect())
                .filter(rect => rect.width > 0 && rect.height > 0);
            if (rects.length === 0) {
                pair.marker.hidden = true;
                return;
            }
            pair.marker.hidden = false;
            const absTop = Math.min(...rects.map(rect => rect.top)) + scrollY;
            const absBottom = Math.max(...rects.map(rect => rect.bottom)) + scrollY;
            const center = (absTop + absBottom) / 2 / docHeight;
            const height = Math.max(6, Math.min(trackHeight, ((absBottom - absTop) / docHeight) * trackHeight));
            const markerTop = Math.max(trackTop, Math.min(trackTop + trackHeight - height, trackTop + center * trackHeight - height / 2));
            pair.marker.style.top = `${markerTop.toFixed(1)}px`;
            pair.marker.style.height = `${height.toFixed(1)}px`;
        });
    };
    const build = () => {
        document.getElementById(markerRootId)?.remove();
        const targets = collectTargets();
        if (targets.length === 0) { pairs = []; return; }
        const rail = document.createElement('div');
        rail.id = markerRootId;
        rail.className = 'rsr-diff-scrollbar';
        pairs = targets.map(target => {
            const marker = document.createElement('div');
            marker.className = 'rsr-diff-scrollbar-marker ' + (target.removed ? 'rsr-diff-scrollbar-marker--removed' : 'rsr-diff-scrollbar-marker--added');
            rail.appendChild(marker);
            return { elements: target.elements, marker };
        });
        document.body.appendChild(rail);
        position();
    };
    let buildPending = false;
    const scheduleBuild = () => {
        if (buildPending) return;
        buildPending = true;
        window.requestAnimationFrame(() => window.requestAnimationFrame(() => {
            buildPending = false;
            build();
        }));
    };
    document.addEventListener('DOMContentLoaded', scheduleBuild, { once: true });
    window.addEventListener('load', scheduleBuild, { once: true });
    window.addEventListener('resize', scheduleBuild, { passive: true });
    window[stateKey] = { scheduleBuild };
    window.setTimeout(scheduleBuild, 250);
})();
""");
    }

    /// <summary>
    /// Splits a Markdown document into its leading YAML frontmatter (between
    /// two <c>---</c> fences on lines of their own) and the body. When no
    /// frontmatter block is found the whole input is treated as body.
    /// </summary>
    private static (string Frontmatter, string Body) SplitFrontmatter(string markdown)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal))
        {
            return (string.Empty, markdown);
        }

        var span = markdown.AsSpan();
        var openLineEnd = span.IndexOf('\n');
        if (openLineEnd < 0)
        {
            return (string.Empty, markdown);
        }
        var openLine = span[..openLineEnd].TrimEnd('\r');
        if (!openLine.SequenceEqual("---"))
        {
            return (string.Empty, markdown);
        }

        var rest = span[(openLineEnd + 1)..];
        var cursor = 0;
        while (cursor < rest.Length)
        {
            var remainder = rest[cursor..];
            var lineEnd = remainder.IndexOf('\n');
            var lineLen = lineEnd < 0 ? remainder.Length : lineEnd;
            var line = remainder[..lineLen].TrimEnd('\r');
            if (line.SequenceEqual("---"))
            {
                var frontmatter = rest[..cursor].ToString();
                var bodyStart = cursor + lineLen + (lineEnd < 0 ? 0 : 1);
                var body = bodyStart >= rest.Length ? string.Empty : rest[bodyStart..].ToString();
                return (frontmatter, body);
            }
            if (lineEnd < 0)
            {
                break;
            }
            cursor += lineEnd + 1;
        }

        // No closing fence — treat the whole document as body so the
        // unterminated YAML block is at least visible.
        return (string.Empty, markdown);
    }

    /// <summary>
    /// Reads a top-level scalar from a YAML frontmatter block (no nested
    /// objects, no anchors). Returns <c>null</c> when the key is missing or
    /// the value spans multiple lines (which we don't support).
    /// </summary>
    private static string? ExtractFrontmatterScalar(string frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter))
        {
            return null;
        }
        using var reader = new StringReader(frontmatter);
        string? line;
        var prefix = key + ":";
        while ((line = reader.ReadLine()) is not null)
        {
            // Top-level keys are not indented; ignore nested entries entirely.
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var rawValue = line[prefix.Length..].Trim();
            if (rawValue.Length == 0)
            {
                return null;
            }
            return UnquoteYaml(rawValue);
        }
        return null;
    }

    private static string UnquoteYaml(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '\'' && value[^1] == '\'') ||
             (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1];
        }
        return value;
    }

    private static string EvaluateLiquidText(
        string source,
        DocsLiquidContext liquidContext,
        DocsVersion version)
        => DocsLiquidEvaluator.Evaluate(source, liquidContext, version);

    private static string RenderInlineWithLiquid(
        string source,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        var evaluated = EvaluateLiquidText(source, liquidContext, version);
        var encoded = WebUtility.HtmlEncode(evaluated);
        return NeutralizeLiquid(encoded);
    }

    /// <summary>
    /// Replaces every Liquid block (<c>{% ... %}</c>) and variable
    /// (<c>{{ ... }}</c>) with a span carrying the original syntax for
    /// reviewer reference. Markdig then sees no template syntax and renders
    /// surrounding prose as expected.
    /// </summary>
    private static string NeutralizeLiquid(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        return NeutralizeLiquidOutsideMarkdownCode(content);
    }

    private static string NeutralizeLiquidOutsideMarkdownCode(string content)
    {
        var lines = SplitMarkdownLines(content);
        var neutralized = new StringBuilder(content.Length + 64);
        var inCodeFence = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                neutralized.Append('\n');
            }

            var line = lines[index];
            var trimmed = line.TrimStart();
            var isCodeFence = trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal);
            neutralized.Append(inCodeFence || isCodeFence
                ? line
                : NeutralizeLiquidOutsideInlineCode(line));
            if (isCodeFence)
            {
                inCodeFence = !inCodeFence;
            }
        }
        return neutralized.ToString();
    }

    private static string NeutralizeLiquidOutsideInlineCode(string line)
    {
        var neutralized = new StringBuilder(line.Length + 32);
        var cursor = 0;
        while (cursor < line.Length)
        {
            var tickStart = line.IndexOf('`', cursor);
            if (tickStart < 0)
            {
                neutralized.Append(NeutralizeLiquidSegment(line[cursor..]));
                break;
            }

            neutralized.Append(NeutralizeLiquidSegment(line[cursor..tickStart]));
            var tickEnd = FindInlineCodeEnd(line, tickStart);
            if (tickEnd < 0)
            {
                neutralized.Append(line[tickStart..]);
                break;
            }

            neutralized.Append(line[tickStart..tickEnd]);
            cursor = tickEnd;
        }
        return neutralized.ToString();
    }

    private static int FindInlineCodeEnd(string line, int tickStart)
    {
        var tickCount = 1;
        while (tickStart + tickCount < line.Length && line[tickStart + tickCount] == '`')
        {
            tickCount++;
        }

        var closing = new string('`', tickCount);
        var closeStart = line.IndexOf(closing, tickStart + tickCount, StringComparison.Ordinal);
        return closeStart < 0 ? -1 : closeStart + tickCount;
    }

    private static string NeutralizeLiquidSegment(string content)
    {
        var blocks = LiquidBlockRegex().Replace(content, static m =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"<span class=\"rsr-liquid\" title=\"Liquid タグ (プレビューでは未評価)\">{{% {WebUtility.HtmlEncode(m.Groups[1].Value)} %}}</span>"));
        var vars = LiquidVariableRegex().Replace(blocks, static m =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"<span class=\"rsr-liquid\" title=\"Liquid 変数 (プレビューでは未評価)\">{{{{ {WebUtility.HtmlEncode(m.Groups[1].Value)} }}}}</span>"));
        return vars;
    }

    private static string RenderOfficialLiquidBlocks(
        string content,
        RenderedHtmlPlaceholderStore? protectedHtmlFragments = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var current = content;
        for (var safety = 0; safety < 16; safety++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = current;
            current = CodeTabsBlockRegex().Replace(
                current,
                match => ProtectRenderedHtml(
                    RenderCodeTabsBlock(match),
                    protectedHtmlFragments));
            current = CodeTabBlockRegex().Replace(
                current,
                match => ProtectRenderedHtml(
                    RenderStandaloneCodeTabBlock(match),
                    protectedHtmlFragments));
            current = SpotlightBlockRegex().Replace(
                current,
                match => ProtectRenderedHtml(
                    RenderSpotlightBlock(match),
                    protectedHtmlFragments));
            current = ToolBlockRegex().Replace(
                current,
                match => ProtectRenderedHtml(
                    RenderToolBlock(match),
                    protectedHtmlFragments));
            current = PromptBlockRegex().Replace(current, RenderPromptBlock);
            if (string.Equals(before, current, StringComparison.Ordinal))
            {
                break;
            }
        }
        return current;
    }

    internal static string PreprocessMarkdownForComparison(
        string content,
        CancellationToken cancellationToken)
    {
        var tableFragmentsExpanded = ExpandMarkdownTableFragments(content, cancellationToken);
        var liquidBlocksRendered = RenderOfficialLiquidBlocks(
            tableFragmentsExpanded,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var githubAlertsRendered = RenderGitHubAlertBlocks(liquidBlocksRendered);
        cancellationToken.ThrowIfCancellationRequested();
        return NeutralizeLiquid(githubAlertsRendered);
    }

    private static string ProtectRenderedHtml(
        string html,
        RenderedHtmlPlaceholderStore? protectedHtmlFragments)
        => protectedHtmlFragments?.Protect(html) ?? html;

    private sealed class RenderedHtmlPlaceholderStore
    {
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly List<string> _fragments = [];

        public string Protect(string html)
        {
            var index = _fragments.Count;
            _fragments.Add(html);
            return "\n<!--rsr-protected-html:"
                + _id
                + ":"
                + index.ToString(CultureInfo.InvariantCulture)
                + "-->\n";
        }

        public string Restore(string html)
        {
            for (var index = _fragments.Count - 1; index >= 0; index--)
            {
                var placeholder = "<!--rsr-protected-html:"
                    + _id
                    + ":"
                    + index.ToString(CultureInfo.InvariantCulture)
                    + "-->";
                html = html.Replace(
                    placeholder,
                    _fragments[index],
                    StringComparison.Ordinal);
            }
            return html;
        }
    }

    private static string RenderGitHubAlertBlocks(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var rendered = new StringBuilder(content.Length);
        var inFence = false;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (IsFenceLine(line))
            {
                inFence = !inFence;
                rendered.AppendLine(line);
                continue;
            }

            if (inFence || !TryReadBlockquoteLine(line, out var quotedLine))
            {
                rendered.AppendLine(line);
                continue;
            }

            var blockquoteLines = new List<string> { quotedLine };
            while (lineIndex + 1 < lines.Length && TryReadBlockquoteLine(lines[lineIndex + 1], out var nextQuotedLine))
            {
                lineIndex++;
                blockquoteLines.Add(nextQuotedLine);
            }

            if (TryRenderGitHubAlertBlock(blockquoteLines, out var alertHtml))
            {
                rendered.AppendLine(alertHtml);
            }
            else
            {
                foreach (var blockquoteLine in blockquoteLines)
                {
                    rendered.Append("> ").AppendLine(blockquoteLine);
                }
            }
        }

        return rendered.ToString();
    }

    private static bool TryRenderGitHubAlertBlock(List<string> blockquoteLines, out string alertHtml)
    {
        alertHtml = string.Empty;
        if (blockquoteLines.Count == 0)
        {
            return false;
        }

        var marker = blockquoteLines[0].Trim();
        if (!TryGetGitHubAlertKind(marker, out var kind, out var label, out var inlineRest))
        {
            return false;
        }

        // GitHub の正式な構文ではマーカーは単独行だが、github/docs では
        // `> [!NOTE] {% data reusables... %}` のようにマーカーと本文が同じ行に
        // 並ぶことがある。マーカー直後の本文を最初の本文行として扱う。
        var bodyLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(inlineRest))
        {
            bodyLines.Add(inlineRest);
        }
        bodyLines.AddRange(blockquoteLines.Skip(1));
        var bodyMarkdown = string.Join('\n', bodyLines).Trim('\n');
        var bodyHtml = bodyMarkdown.Length == 0
            ? string.Empty
            : Markdown.ToHtml(RenderGitHubAlertBlocks(bodyMarkdown), _pipeline).TrimEnd();
        alertHtml = string.Create(
            CultureInfo.InvariantCulture,
            $"\n<div class=\"ghd-markdown-alert ghd-markdown-alert-{kind}\">\n<p class=\"ghd-markdown-alert-title\">{WebUtility.HtmlEncode(label)}</p>\n{bodyHtml}\n</div>\n");
        return true;
    }

    private static bool TryGetGitHubAlertKind(string marker, out string kind, out string label, out string inlineRest)
    {
        kind = string.Empty;
        label = string.Empty;
        inlineRest = string.Empty;
        if (marker.Length < 4 || marker[0] != '[' || marker[1] != '!')
        {
            return false;
        }

        var close = marker.IndexOf(']', 2);
        if (close < 3)
        {
            return false;
        }

        var alertType = marker[2..close].Trim();
        (kind, label) = alertType.ToUpperInvariant() switch
        {
            "NOTE" => ("note", "Note"),
            "TIP" => ("tip", "Tip"),
            "IMPORTANT" => ("important", "Important"),
            "WARNING" => ("warning", "Warning"),
            "CAUTION" => ("caution", "Caution"),
            _ => (string.Empty, string.Empty),
        };
        if (kind.Length == 0)
        {
            return false;
        }

        inlineRest = marker[(close + 1)..].Trim();
        return true;
    }

    private static bool TryReadBlockquoteLine(string line, out string quotedLine)
    {
        quotedLine = string.Empty;
        var cursor = 0;
        while (cursor < line.Length && (line[cursor] == ' ' || line[cursor] == '\t'))
        {
            cursor++;
        }

        if (cursor >= line.Length || line[cursor] != '>')
        {
            return false;
        }

        cursor++;
        if (cursor < line.Length && line[cursor] == ' ')
        {
            cursor++;
        }
        quotedLine = cursor >= line.Length ? string.Empty : line[cursor..];
        return true;
    }

    private static bool IsFenceLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static string RenderSpotlightBlock(Match match)
    {
        var tag = match.Groups["tag"].Value;
        var color = tag switch
        {
            "note" => "accent",
            "tip" => "success",
            "warning" => "attention",
            "danger" => "danger",
            _ => "accent",
        };
        var innerHtml = RenderLiquidBlockBody(match.Groups["body"].Value);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"\n<div class=\"ghd-alert ghd-alert-{color} ghd-spotlight-{color}\">\n{innerHtml}\n</div>\n");
    }

    private static string RenderToolBlock(Match match)
    {
        var tag = match.Groups["tag"].Value;
        var innerHtml = RenderLiquidBlockBody(match.Groups["body"].Value);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"\n<div class=\"ghd-tool {WebUtility.HtmlEncode(tag)}\">\n{innerHtml}\n</div>\n");
    }

    private static string RenderPromptBlock(Match match)
    {
        var prompt = match.Groups["body"].Value.Trim();
        var promptId = BuildPromptId(prompt);
        var href = "https://github.com/copilot?prompt=" + Uri.EscapeDataString(prompt);
        var encodedPrompt = WebUtility.HtmlEncode(prompt);
        var encodedHref = WebUtility.HtmlEncode(href);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"<code id=\"{promptId}\">{encodedPrompt}</code><a href=\"{encodedHref}\" target=\"_blank\" class=\"tooltipped tooltipped-n ml-1 copilot-prompt-long\" aria-label=\"Run this prompt in Copilot Chat\" aria-describedby=\"{promptId}\" style=\"text-decoration:none;\">{_copilotOcticonSvg}</a><a href=\"{encodedHref}\" target=\"_blank\" class=\"tooltipped tooltipped-n ml-1 copilot-prompt-short\" aria-label=\"Run prompt\" aria-describedby=\"{promptId}\" style=\"text-decoration:none;\">{_copilotOcticonSvg}</a>");
    }

    private static string RenderCodeTabsBlock(Match match)
    {
        var body = match.Groups["body"].Value;
        var tabMatches = CodeTabBlockRegex().Matches(body);
        if (tabMatches.Count == 0)
        {
            return RenderLiquidBlockBody(body);
        }

        var html = new StringBuilder(body.Length + 256);
        html.AppendLine("\n<div class=\"ghd-code-tabs\">");
        foreach (Match tabMatch in tabMatches)
        {
            html.Append(RenderCodeTabBlock(tabMatch));
        }
        html.AppendLine("</div>\n");
        return html.ToString();
    }

    private static string RenderStandaloneCodeTabBlock(Match match)
        => "\n<div class=\"ghd-code-tabs\">\n" + RenderCodeTabBlock(match) + "</div>\n";

    private static string RenderCodeTabBlock(Match match)
    {
        var label = NormalizeCodeTabLabel(match.Groups["label"].Value);
        var innerHtml = RenderLiquidBlockBody(match.Groups["body"].Value);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"<section class=\"ghd-code-tab\"><div class=\"ghd-code-tab-label\">{WebUtility.HtmlEncode(label)}</div><div class=\"ghd-code-tab-body\">\n{innerHtml}\n</div></section>\n");
    }

    private static string NormalizeCodeTabLabel(string label)
    {
        var trimmed = label.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1];
        }
        return trimmed.Length == 0 ? "code" : trimmed;
    }

    private static string RenderLiquidBlockBody(string body)
    {
        var nestedBlocksRendered = RenderOfficialLiquidBlocks(body.Trim('\r', '\n'));
        var neutralized = NeutralizeLiquid(nestedBlocksRendered);
        return Markdown.ToHtml(neutralized, _pipeline).TrimEnd();
    }

    private static string BuildPromptId(string prompt)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(prompt))
        {
            hash ^= b;
            hash *= prime;
        }
        return string.Create(CultureInfo.InvariantCulture, $"copilot-prompt-{hash:x8}");
    }

    private static string RewriteAutotitleLinks(
        string html,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        if (string.IsNullOrEmpty(html) || liquidContext.PageTitles.Count == 0)
        {
            return html;
        }

        return AnchorRegex().Replace(html, match =>
        {
            var innerHtml = match.Groups["body"].Value;
            if (!IsAutotitleLinkBody(innerHtml))
            {
                return match.Value;
            }

            var attrs = match.Groups["attrs"].Value;
            var hrefMatch = AnchorHrefRegex().Match(attrs);
            if (!hrefMatch.Success)
            {
                return match.Value;
            }

            var href = WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value);
            var rawTitle = ResolveAutotitleRawTitle(href, repoPath, liquidContext.PageTitles);
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                return match.Value;
            }

            var titleHtml = RenderInlineWithLiquid(rawTitle, liquidContext, version).Trim();
            if (titleHtml.Length == 0)
            {
                return match.Value;
            }

            var diffClass = TryGetAutotitleDiffClass(innerHtml);
            var replacementBody = diffClass is null
                ? titleHtml
                : string.Concat("<span class=\"", diffClass, "\">", titleHtml, "</span>");

            return string.Concat("<a", attrs, ">", replacementBody, "</a>");
        });
    }

    internal static string RewriteAutotitleMarkdownLinks(
        string markdown,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        if (string.IsNullOrEmpty(markdown) || liquidContext.PageTitles.Count == 0)
        {
            return markdown;
        }

        return RewriteAutotitleMarkdownLinksOutsideMarkdownCode(markdown, repoPath, liquidContext, version);
    }

    private static string RewriteAutotitleMarkdownLinksOutsideMarkdownCode(
        string content,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        var lines = SplitMarkdownLines(content);
        var rewritten = new StringBuilder(content.Length + 64);
        var inCodeFence = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                rewritten.Append('\n');
            }

            var line = lines[index];
            var trimmed = line.TrimStart();
            var isCodeFence = trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal);
            rewritten.Append(inCodeFence || isCodeFence
                ? line
                : RewriteAutotitleMarkdownLinksOutsideInlineCode(line, repoPath, liquidContext, version));
            if (isCodeFence)
            {
                inCodeFence = !inCodeFence;
            }
        }
        return rewritten.ToString();
    }

    private static string RewriteAutotitleMarkdownLinksOutsideInlineCode(
        string line,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        var rewritten = new StringBuilder(line.Length + 32);
        var cursor = 0;
        while (cursor < line.Length)
        {
            var tickStart = line.IndexOf('`', cursor);
            if (tickStart < 0)
            {
                rewritten.Append(RewriteAutotitleMarkdownLinkSegment(line[cursor..], repoPath, liquidContext, version));
                break;
            }

            rewritten.Append(RewriteAutotitleMarkdownLinkSegment(line[cursor..tickStart], repoPath, liquidContext, version));
            var tickEnd = FindInlineCodeEnd(line, tickStart);
            if (tickEnd < 0)
            {
                rewritten.Append(line[tickStart..]);
                break;
            }

            rewritten.Append(line[tickStart..tickEnd]);
            cursor = tickEnd;
        }
        return rewritten.ToString();
    }

    private static string RewriteAutotitleMarkdownLinkSegment(
        string content,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
        => FullAutotitleMarkdownLinkRegex().Replace(content, match =>
        {
            var destination = match.Groups["destination"].Value;
            var href = destination.Length >= 2 && destination[0] == '<' && destination[^1] == '>'
                ? destination[1..^1]
                : destination;
            var rawTitle = ResolveAutotitleRawTitle(href, repoPath, liquidContext.PageTitles);
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                return match.Value;
            }

            var titleText = EvaluateLiquidText(rawTitle, liquidContext, version).Trim();
            if (titleText.Length == 0)
            {
                return match.Value;
            }

            var titleAttribute = ExtractMarkdownLinkTitleAttribute(match.Groups["suffix"].Value);
            var attrs = match.Groups["attrs"].Value;
            if (attrs.Length > 0)
            {
                return string.Concat(
                    "<a href=\"",
                    WebUtility.HtmlEncode(href),
                    "\"",
                    titleAttribute,
                    "><span",
                    attrs,
                    ">",
                    WebUtility.HtmlEncode(titleText),
                    "</span></a>");
            }

            return string.Concat(
                "<a href=\"",
                WebUtility.HtmlEncode(href),
                "\"",
                titleAttribute,
                ">",
                WebUtility.HtmlEncode(titleText),
                "</a>");
        });

    private static string ExtractMarkdownLinkTitleAttribute(string suffix)
    {
        var title = ExtractMarkdownLinkTitle(suffix);
        return title is null
            ? string.Empty
            : string.Concat(" title=\"", WebUtility.HtmlEncode(title), "\"");
    }

    private static string? ExtractMarkdownLinkTitle(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return null;
        }

        var trimmed = suffix.Trim();
        if (trimmed.EndsWith(')'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }
        if (trimmed.Length < 2)
        {
            return null;
        }

        if (trimmed[0] is '"' or '\'')
        {
            var quote = trimmed[0];
            var end = trimmed.IndexOf(quote, 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        if (trimmed[0] == '(' && trimmed[^1] == ')' && trimmed.Length > 2)
        {
            return trimmed[1..^1];
        }

        return null;
    }

    private static bool IsAutotitleLinkBody(string innerHtml)
    {
        var text = HtmlTagRegex().Replace(innerHtml, string.Empty);
        return string.Equals(WebUtility.HtmlDecode(text).Trim(), "AUTOTITLE", StringComparison.Ordinal);
    }

    private static string? TryGetAutotitleDiffClass(string innerHtml)
    {
        var match = AutotitleSpanRegex().Match(innerHtml);
        if (!match.Success)
        {
            return null;
        }

        var attrs = match.Groups["attrs"].Value;
        if (attrs.Contains("rsr-rendered-diff-added", StringComparison.Ordinal))
        {
            return "rsr-rendered-diff-added";
        }
        if (attrs.Contains("rsr-rendered-diff-removed", StringComparison.Ordinal))
        {
            return "rsr-rendered-diff-removed";
        }
        return null;
    }

    private static string? ResolveAutotitleRawTitle(
        string href,
        string repoPath,
        IReadOnlyDictionary<string, string> pageTitles)
    {
        var normalizedHref = NormalizeAutotitleHref(href, repoPath);
        if (normalizedHref is null)
        {
            return null;
        }

        foreach (var candidate in BuildAutotitleLookupCandidates(normalizedHref))
        {
            if (pageTitles.TryGetValue(candidate, out var title))
            {
                return title;
            }
        }

        if (normalizedHref.EndsWith("...", StringComparison.Ordinal))
        {
            var prefix = normalizedHref[..^3].Trim('/');
            foreach (var candidatePrefix in BuildAutotitleTruncatedLookupPrefixes(prefix))
            {
                foreach (var pair in pageTitles)
                {
                    if (pair.Key.StartsWith(candidatePrefix, StringComparison.Ordinal))
                    {
                        return pair.Value;
                    }
                }
            }
        }
        return null;
    }

    private static string? NormalizeAutotitleHref(string href, string repoPath)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var trimmed = href.Trim();
        if (trimmed.StartsWith('#'))
        {
            return null;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var pathWithSuffix = trimmed;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            if (!string.Equals(absoluteUri.Host, "docs.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            pathWithSuffix = absoluteUri.AbsolutePath;
        }

        var suffixStart = FindUrlSuffixStart(pathWithSuffix);
        var path = suffixStart < 0 ? pathWithSuffix : pathWithSuffix[..suffixStart];
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var isRootRelative = path.StartsWith('/');
        var unescapedPath = Uri.UnescapeDataString(path).Replace('\\', '/');
        var combined = isRootRelative
            ? unescapedPath.TrimStart('/')
            : CombineAssetPath(GetRepoDirectory(repoPath), unescapedPath);
        combined = RemoveDocsRoutePrefix(combined);
        return NormalizeAssetPath(combined);
    }

    private static string RemoveDocsRoutePrefix(string path)
    {
        var normalized = path.Trim('/');
        if (normalized.Length == 0)
        {
            return normalized;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0)
        {
            return normalized;
        }

        if (string.Equals(segments[0], "en", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(0);
        }

        if (segments.Count > 0 && segments[0].Contains('@'))
        {
            segments.RemoveAt(0);
        }

        return string.Join('/', segments);
    }

    private static IEnumerable<string> BuildAutotitleLookupCandidates(string normalizedHref)
    {
        var trimmed = normalizedHref.Trim('/');
        if (trimmed.Length == 0)
        {
            yield break;
        }

        yield return trimmed;
        yield return "/" + trimmed;

        if (trimmed.StartsWith("content/", StringComparison.Ordinal))
        {
            if (trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                yield return trimmed;
            }
            else
            {
                yield return trimmed + ".md";
                yield return trimmed + "/index.md";
            }
            yield break;
        }

        yield return "content/" + trimmed + ".md";
        yield return "content/" + trimmed + "/index.md";
    }

    private static IEnumerable<string> BuildAutotitleTruncatedLookupPrefixes(string normalizedHrefPrefix)
    {
        var trimmed = normalizedHrefPrefix.Trim('/');
        if (trimmed.Length == 0)
        {
            yield break;
        }

        yield return trimmed;
        yield return "/" + trimmed;

        if (trimmed.StartsWith("content/", StringComparison.Ordinal))
        {
            yield return trimmed;
            yield break;
        }

        yield return "content/" + trimmed;
    }

    internal static string RewriteLocalReferencesForComparison(string html, string repoPath)
        => RewriteHtmlReferences(
            html,
            repoPath,
            "/markdown-assets",
            "/markdown-links");

    private static string RewriteAssetReferences(string html, string repoPath, string? assetBasePath)
        => string.IsNullOrWhiteSpace(assetBasePath)
            ? html
            : RewriteHtmlReferences(html, repoPath, assetBasePath, linkBasePath: null);

    private static string RewriteHtmlReferences(
        string html,
        string repoPath,
        string assetBasePath,
        string? linkBasePath)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        var rewritten = new StringBuilder(html.Length + 128);
        string? rawTextTagName = null;
        var index = 0;
        while (index < html.Length)
        {
            if (rawTextTagName is not null)
            {
                var rawTextEnd = FindRawTextClosingTag(html, index, rawTextTagName);
                if (rawTextEnd < 0)
                {
                    rewritten.Append(html, index, html.Length - index);
                    break;
                }
                rewritten.Append(html, index, rawTextEnd - index);
                index = rawTextEnd;
                rawTextTagName = null;
            }

            var tagStart = html.IndexOf('<', index);
            if (tagStart < 0)
            {
                rewritten.Append(html, index, html.Length - index);
                break;
            }
            rewritten.Append(html, index, tagStart - index);

            if (html.AsSpan(tagStart).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = html.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                var nextIndex = commentEnd < 0 ? html.Length : commentEnd + 3;
                rewritten.Append(html, tagStart, nextIndex - tagStart);
                index = nextIndex;
                continue;
            }

            var tagEnd = FindHtmlTagEnd(html, tagStart);
            var tag = html[tagStart..tagEnd];
            var tagName = GetHtmlTagName(tag, out var isClosing);
            if (!isClosing && tagName.Length > 0)
            {
                tag = RewriteTagAttributes(
                    tag,
                    repoPath,
                    assetBasePath,
                    tagName == "a" ? linkBasePath : null);
            }
            rewritten.Append(tag);
            index = tagEnd;

            if (!isClosing
                && IsRawTextElement(tagName)
                && !tag.AsSpan().TrimEnd().EndsWith("/>", StringComparison.Ordinal))
            {
                rawTextTagName = tagName;
            }
        }
        return rewritten.ToString();
    }

    private static string RewriteTagAttributes(
        string tag,
        string repoPath,
        string assetBasePath,
        string? linkBasePath)
    {
        StringBuilder? rewritten = null;
        var copiedThrough = 0;
        var index = 1;
        while (index < tag.Length && tag[index] is '/' or '!')
        {
            index++;
        }
        while (index < tag.Length && !char.IsWhiteSpace(tag[index]) && tag[index] != '>')
        {
            index++;
        }

        while (index < tag.Length)
        {
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }
            if (index >= tag.Length || tag[index] is '>' or '/')
            {
                break;
            }

            var nameStart = index;
            while (index < tag.Length
                   && !char.IsWhiteSpace(tag[index])
                   && tag[index] is not '=' and not '>' and not '/')
            {
                index++;
            }
            if (index == nameStart)
            {
                index++;
                continue;
            }

            var attributeName = tag[nameStart..index];
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }
            if (index >= tag.Length || tag[index] != '=')
            {
                continue;
            }
            index++;
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            var quote = index < tag.Length && tag[index] is '\'' or '"' ? tag[index++] : '\0';
            var valueStart = index;
            if (quote == '\0')
            {
                while (index < tag.Length
                       && !char.IsWhiteSpace(tag[index])
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
            var valueEnd = index;
            if (index < tag.Length && quote != '\0')
            {
                index++;
            }

            var decodedValue = WebUtility.HtmlDecode(tag[valueStart..valueEnd]);
            var next = attributeName.ToLowerInvariant() switch
            {
                "src" or "poster" => RewriteAssetUrl(decodedValue, repoPath, assetBasePath),
                "srcset" => RewriteSrcSet(decodedValue, repoPath, assetBasePath),
                "href" when linkBasePath is not null => RewriteAssetUrl(decodedValue, repoPath, linkBasePath),
                _ => decodedValue,
            };
            if (string.Equals(next, decodedValue, StringComparison.Ordinal))
            {
                continue;
            }

            rewritten ??= new StringBuilder(tag.Length + 64);
            rewritten
                .Append(tag, copiedThrough, valueStart - copiedThrough)
                .Append(WebUtility.HtmlEncode(next));
            copiedThrough = valueEnd;
        }

        return rewritten is null
            ? tag
            : rewritten.Append(tag, copiedThrough, tag.Length - copiedThrough).ToString();
    }

    private static bool IsRawTextElement(string tagName)
        => tagName is "script"
            or "style"
            or "textarea"
            or "title"
            or "iframe"
            or "noembed"
            or "noframes"
            or "xmp"
            or "plaintext";

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

    private static string GetHtmlTagName(string tag, out bool isClosing)
    {
        var index = 1;
        isClosing = index < tag.Length && tag[index] == '/';
        if (isClosing)
        {
            index++;
        }
        while (index < tag.Length && char.IsWhiteSpace(tag[index]))
        {
            index++;
        }
        var nameStart = index;
        while (index < tag.Length
               && !char.IsWhiteSpace(tag[index])
               && tag[index] is not '>' and not '/')
        {
            index++;
        }
        return tag[nameStart..index].ToLowerInvariant();
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
                || char.IsWhiteSpace(html[afterName]))
            {
                return index;
            }
            index = afterName;
        }
        return -1;
    }

    private static string RewriteSrcSet(string srcset, string repoPath, string assetBasePath)
    {
        if (srcset.Contains("data:", StringComparison.OrdinalIgnoreCase))
        {
            return srcset;
        }

        var candidates = srcset.Split(',', StringSplitOptions.None);
        var rewritten = new StringBuilder(srcset.Length + 64);
        for (var i = 0; i < candidates.Length; i++)
        {
            if (i > 0)
            {
                rewritten.Append(',');
            }

            var candidate = candidates[i];
            var leadingLength = candidate.Length - candidate.TrimStart().Length;
            var trailingLength = candidate.Length - candidate.TrimEnd().Length;
            var leading = candidate[..leadingLength];
            var core = candidate[leadingLength..(candidate.Length - trailingLength)];
            var trailing = candidate[(candidate.Length - trailingLength)..];
            if (core.Length == 0)
            {
                rewritten.Append(candidate);
                continue;
            }

            var descriptorStart = FindFirstWhitespace(core);
            var url = descriptorStart < 0 ? core : core[..descriptorStart];
            var descriptor = descriptorStart < 0 ? string.Empty : core[descriptorStart..];
            rewritten
                .Append(leading)
                .Append(RewriteAssetUrl(url, repoPath, assetBasePath))
                .Append(descriptor)
                .Append(trailing);
        }
        return rewritten.ToString();
    }

    private static string RewriteAssetUrl(string url, string repoPath, string assetBasePath)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith('#')
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return url;
        }

        var suffixStart = FindUrlSuffixStart(trimmed);
        var path = suffixStart < 0 ? trimmed : trimmed[..suffixStart];
        var suffix = suffixStart < 0 ? string.Empty : trimmed[suffixStart..];
        if (string.IsNullOrWhiteSpace(path))
        {
            return url;
        }

        var repoRelative = ResolveRepoRelativeAssetPath(repoPath, path);
        if (repoRelative is null)
        {
            return url;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{assetBasePath.TrimEnd('/')}/{EscapeAssetPath(repoRelative)}{suffix}");
    }

    private static int FindUrlSuffixStart(string url)
    {
        var queryIndex = url.IndexOf('?');
        var fragmentIndex = url.IndexOf('#');
        return (queryIndex, fragmentIndex) switch
        {
            (>= 0, >= 0) => Math.Min(queryIndex, fragmentIndex),
            (>= 0, _) => queryIndex,
            (_, >= 0) => fragmentIndex,
            _ => -1,
        };
    }

    private static string? ResolveRepoRelativeAssetPath(string repoPath, string assetPath)
    {
        var normalizedAssetPath = assetPath.Replace('\\', '/');
        var combined = normalizedAssetPath.StartsWith('/')
            ? normalizedAssetPath.TrimStart('/')
            : CombineAssetPath(GetRepoDirectory(repoPath), normalizedAssetPath);
        return NormalizeAssetPath(combined);
    }

    private static string GetRepoDirectory(string repoPath)
    {
        var normalized = repoPath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : normalized[..lastSlash];
    }

    private static int FindFirstWhitespace(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                return i;
            }
        }
        return -1;
    }

    private static string CombineAssetPath(string basePath, string relativePath)
        => string.IsNullOrEmpty(basePath) ? relativePath : basePath + "/" + relativePath;

    private static string? NormalizeAssetPath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals(".", StringComparison.Ordinal))
            {
                continue;
            }
            if (segment.Equals("..", StringComparison.Ordinal))
            {
                if (segments.Count == 0)
                {
                    return null;
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static string EscapeAssetPath(string repoRelativePath)
    {
        var segments = repoRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var escaped = new StringBuilder(repoRelativePath.Length + segments.Length * 2);
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                escaped.Append('/');
            }
            escaped.Append(Uri.EscapeDataString(Uri.UnescapeDataString(segments[i])));
        }
        return escaped.ToString();
    }

    private static bool HasVisibleBodyMarkup(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(HtmlCommentRegex().Replace(html, string.Empty));
    }

    private static string RestoreEscapedRenderedDiffMarkers(string html)
        => EscapedRenderedDiffMarkerRegex().Replace(
            html,
            static match =>
            {
                var markerClass = match.Groups["class"].Value;
                var ariaHidden = markerClass.Contains("rsr-rendered-diff-gap", StringComparison.Ordinal)
                    ? " aria-hidden=\"true\""
                    : string.Empty;
                return "<span class=\"" + markerClass + "\"" + ariaHidden + ">" + match.Groups["body"].Value + "</span>";
            });

    private static string ShortSha(string sha)
        => sha.Length <= 7 ? sha : sha[..7];

    /// <summary>
    /// Appends the "影響を受ける版" badge bar to <paramref name="html"/> when
    /// <paramref name="affectedVersions"/> is non-null (the caller wants the
    /// UI). The currently-rendered version is highlighted so the reviewer
    /// can tell which badge corresponds to what they're looking at; the
    /// remaining badges signal versions where the PR also changes content
    /// but the reviewer is not currently viewing — preventing them from
    /// missing per-version diffs (IMPLEMENTATION_PLAN.md §Step 19.9).
    /// </summary>
    private static void AppendVersionBadgeMarkup(
        StringBuilder html,
        DocsVersion currentVersion,
        IReadOnlyList<DocsVersion>? affectedVersions)
    {
        if (affectedVersions is null)
        {
            return;
        }

        html.Append("<div class=\"rsr-version-bar\" data-testid=\"rsr-version-bar\">");
        html.Append("<span class=\"rsr-version-current\">表示中: ")
            .Append(WebUtility.HtmlEncode(currentVersion.DisplayLabel))
            .Append("</span>");

        if (affectedVersions.Count == 0)
        {
            html.Append("<span class=\"rsr-version-empty\">本文レンダリング差分はありません。</span>");
        }
        else
        {
            html.Append("<span class=\"rsr-version-impact-label\">この PR で差分のある版:</span>");
            html.Append("<ul class=\"rsr-version-badges\">");
            foreach (var version in affectedVersions)
            {
                var isCurrent = version == currentVersion;
                html.Append("<li><button type=\"button\" class=\"rsr-version-badge");
                if (isCurrent)
                {
                    html.Append(" rsr-version-badge--current");
                }
                html.Append("\" data-rsr-version-slug=\"")
                    .Append(WebUtility.HtmlEncode(version.Slug))
                    .Append("\" data-version-slug=\"")
                    .Append(WebUtility.HtmlEncode(version.Slug))
                    .Append('"');
                if (isCurrent)
                {
                    html.Append(" aria-current=\"true\" aria-label=\"")
                        .Append(WebUtility.HtmlEncode($"{version.DisplayLabel} を表示中"))
                        .Append('"');
                }
                else
                {
                    html.Append(" aria-label=\"")
                        .Append(WebUtility.HtmlEncode($"{version.DisplayLabel} に切り替え"))
                        .Append('"');
                }
                html.Append('>')
                    .Append(WebUtility.HtmlEncode(version.DisplayLabel))
                    .Append("</button></li>");
            }
            html.Append("</ul>");
        }
        html.AppendLine("</div>");
    }

    private static void AppendVersionDiffSummary(
        StringBuilder html,
        DocsVersion currentVersion,
        IReadOnlyList<DocsVersionImpactDetail>? versionImpacts,
        string repoPath,
        DocsLiquidContext liquidContext)
    {
        if (versionImpacts is null || versionImpacts.Count == 0)
        {
            return;
        }

        // 表示中の版を含むグループは、本文のインライン差分 (赤/緑マーカー) で
        // すでに見えているため重複になる。ここでは「表示中の版には出ないが、
        // 他の版だけで変わる変更」だけを残し、レビュアーが見落としやすい
        // 他版限定の差分に集中できるようにする (IMPLEMENTATION_PLAN.md §Step 19.9)。
        var groups = BuildVersionImpactGroups(versionImpacts);
        var otherVersionGroups = groups
            .Where(group => !group.Versions.Contains(currentVersion))
            .ToList();
        if (otherVersionGroups.Count == 0)
        {
            return;
        }

        html.Append("<section class=\"rsr-version-diff-summary\" data-testid=\"rsr-version-diff-summary\" aria-label=\"版別差分\">");
        html.Append("<h2>変更パターン</h2>");
        html.Append("<p class=\"rsr-version-diff-overview\">");
        if (otherVersionGroups.Count == 1)
        {
            html.Append("表示中の版には出ない、他の版だけの変更です。本文の差分には含まれないため、ここで内容を確認してください。");
        }
        else
        {
            html.Append(otherVersionGroups.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" 種類の他版限定の変更があります。表示中の版の本文には出ないため、ここで確認してください。");
        }
        html.Append("</p>");
        html.Append("<ul class=\"rsr-version-diff-list\">");
        for (var groupIndex = 0; groupIndex < otherVersionGroups.Count; groupIndex++)
        {
            var group = otherVersionGroups[groupIndex];
            html.Append("<li class=\"rsr-version-diff-item\">");
            html.Append("<h3 class=\"rsr-version-diff-title\"><span>")
                .Append(WebUtility.HtmlEncode(BuildVersionImpactGroupTitle(group, groupIndex)))
                .Append("</span></h3>");

            AppendVersionPatternBadges(html, group.Versions);

            var visibleChanges = group.Changes.Take(3).ToArray();
            foreach (var change in visibleChanges)
            {
                AppendVersionChange(html, change, repoPath, liquidContext, currentVersion);
            }
            if (group.Changes.Count > visibleChanges.Length)
            {
                html.Append("<p class=\"rsr-version-diff-more\">")
                    .Append((group.Changes.Count - visibleChanges.Length).ToString(CultureInfo.InvariantCulture))
                    .Append(" 件の追加差分があります</p>");
            }
            html.Append("</li>");
        }
        html.AppendLine("</ul></section>");
    }

    private static void AppendSourceDiffSummary(
        StringBuilder html,
        MarkdownSourceDiffSummary? sourceDiff,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        if (sourceDiff is null || !sourceDiff.HasChanges)
        {
            return;
        }

        // 表示中の版で本文に反映される ifversion 変更は、本文の差分側で既に
        // 見えている。ここでは「表示中の版では本文に出ない条件変更」だけを残し、
        // 見落とし防止に集中する。
        var hiddenIfversionChanges = sourceDiff.IfversionChanges
            .Where(change => !IfversionChangeRendersForVersion(change, version, liquidContext.Features))
            .ToArray();
        if (hiddenIfversionChanges.Length == 0 && sourceDiff.RelatedFileChanges.Count == 0)
        {
            return;
        }

        var totalChanges = hiddenIfversionChanges.Length
            + sourceDiff.RelatedFileChanges.Sum(static file => file.Changes.Count);
        html.Append("<section class=\"rsr-source-diff\" data-testid=\"rsr-source-diff\" aria-label=\"ソース差分\">");
        html.Append("<h2>レンダリングに出ないソース差分</h2>");
        html.Append("<p class=\"rsr-source-diff-overview\">")
            .Append(totalChanges.ToString(CultureInfo.InvariantCulture))
            .Append(" 件の Liquid 条件または関連 data ファイル差分があります。表示中の版では本文に出ないため、この条件変更を確認してください。</p>");
        html.Append("<ul class=\"rsr-source-diff-list\">");

        foreach (var change in hiddenIfversionChanges.Take(8))
        {
            AppendIfversionSourceChange(html, change, repoPath, liquidContext, version);
        }

        foreach (var fileChange in sourceDiff.RelatedFileChanges.Take(4))
        {
            html.Append("<li class=\"rsr-source-change\" data-change-kind=\"updated\">");
            html.Append("<span class=\"rsr-source-change-kind\">関連 feature 定義</span>");
            html.Append("<p class=\"rsr-source-file\">")
                .Append(WebUtility.HtmlEncode(fileChange.Path))
                .Append("</p>");
            foreach (var lineChange in fileChange.Changes.Take(6))
            {
                AppendSourceLineChange(html, lineChange, repoPath, liquidContext, version);
            }
            if (fileChange.Changes.Count > 6)
            {
                html.Append("<p class=\"rsr-version-diff-more\">")
                    .Append((fileChange.Changes.Count - 6).ToString(CultureInfo.InvariantCulture))
                    .Append(" 件の追加差分があります</p>");
            }
            html.Append("</li>");
        }

        if (hiddenIfversionChanges.Length > 8 || sourceDiff.RelatedFileChanges.Count > 4)
        {
            html.Append("<li class=\"rsr-version-diff-more\">一部のソース差分のみ表示しています。</li>");
        }
        html.Append("</ul></section>");
    }

    private static readonly HashSet<string> _versionExpressionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and",
        "or",
        "not",
        "fpt",
        "ghec",
        "ghes",
        "ghae",
    };

    /// <summary>
    /// <paramref name="change"/> の <c>ifversion</c> 条件が、現在表示中の
    /// <paramref name="version"/> の本文レンダリングに反映されるか（=本文の差分側で
    /// 既に見えるか）を返す。<c>true</c> のときソース差分セクションからは除外する。
    /// </summary>
    private static bool IfversionChangeRendersForVersion(
        MarkdownIfversionChange change,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features)
    {
        var beforeRenders = EvaluateIfversionExpressionForVersion(change.BeforeExpression, version, features);
        var afterRenders = EvaluateIfversionExpressionForVersion(change.AfterExpression, version, features);
        return change.Kind switch
        {
            // 追加: 表示中の版が条件を満たすなら本文に出る → 本文差分側で見える。
            DocsVersionChangeKind.Added => afterRenders == true,
            // 削除: 表示中の版が以前は条件を満たしていたなら本文から消える → 本文差分側で見える。
            DocsVersionChangeKind.Removed => beforeRenders == true,
            // 条件変更: 表示中の版での可視状態が反転する場合のみ本文に差分が出る。
            DocsVersionChangeKind.Updated => beforeRenders is bool before && afterRenders is bool after && before != after,
            _ => false,
        };
    }

    /// <summary>
    /// <c>ifversion</c> 式を <paramref name="version"/> で評価する。未知の feature
    /// フラグを含む式は確実に評価できないため <c>null</c> を返し、呼び出し側で安全側
    /// （ソース差分を出し続ける）に倒せるようにする。
    /// </summary>
    private static bool? EvaluateIfversionExpressionForVersion(
        string? expression,
        DocsVersion version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> features)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        foreach (Match match in VersionExpressionIdentifierRegex().Matches(expression))
        {
            var identifier = match.Value;
            if (_versionExpressionKeywords.Contains(identifier))
            {
                continue;
            }
            if (!features.ContainsKey(identifier))
            {
                return null;
            }
        }

        return VersionExpressionEvaluator.Evaluate(expression, version, features);
    }

    private static void AppendIfversionSourceChange(
        StringBuilder html,
        MarkdownIfversionChange change,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        html.Append("<li class=\"rsr-source-change\" data-change-kind=\"")
            .Append(WebUtility.HtmlEncode(BuildChangeKindSlug(change.Kind)))
            .Append("\">");
        html.Append("<span class=\"rsr-source-change-kind\">")
            .Append(WebUtility.HtmlEncode($"ifversion {BuildChangeKindLabel(change.Kind)}"))
            .Append("</span>");
        if (!string.IsNullOrWhiteSpace(change.BeforeExpression))
        {
            AppendSourceLine(html, "変更前", "{% ifversion " + change.BeforeExpression + " %}", repoPath, liquidContext, version);
        }
        if (!string.IsNullOrWhiteSpace(change.BeforePreview))
        {
            AppendSourceLine(html, "対象本文", change.BeforePreview, repoPath, liquidContext, version);
        }
        if (!string.IsNullOrWhiteSpace(change.AfterExpression))
        {
            AppendSourceLine(html, "PR HEAD", "{% ifversion " + change.AfterExpression + " %}", repoPath, liquidContext, version);
        }
        if (!string.IsNullOrWhiteSpace(change.AfterPreview)
            && !string.Equals(change.BeforePreview, change.AfterPreview, StringComparison.Ordinal))
        {
            AppendSourceLine(html, "対象本文", change.AfterPreview, repoPath, liquidContext, version);
        }
        html.Append("</li>");
    }

    private static void AppendSourceLineChange(
        StringBuilder html,
        MarkdownSourceLineChange change,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        if (!string.IsNullOrWhiteSpace(change.BeforeLine))
        {
            AppendSourceLine(html, "変更前", change.BeforeLine, repoPath, liquidContext, version);
        }
        if (!string.IsNullOrWhiteSpace(change.AfterLine))
        {
            AppendSourceLine(html, "PR HEAD", change.AfterLine, repoPath, liquidContext, version);
        }
    }

    private static void AppendSourceLine(
        StringBuilder html,
        string label,
        string line,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        var displayLine = RewriteAutotitlePlainTextLine(line, repoPath, liquidContext, version);
        html.Append("<p class=\"rsr-source-line\"><span class=\"rsr-source-line-label\">")
            .Append(WebUtility.HtmlEncode(label))
            .Append("</span><code>")
            .Append(WebUtility.HtmlEncode(displayLine))
            .Append("</code></p>");
    }

    private static string RewriteAutotitlePlainTextLine(
        string line,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        if (string.IsNullOrEmpty(line) || liquidContext.PageTitles.Count == 0)
        {
            return line;
        }

        return AutotitleMarkdownLinkRegex().Replace(line, match =>
        {
            var rawTitle = ResolveAutotitleRawTitle(match.Groups["href"].Value, repoPath, liquidContext.PageTitles);
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                return "the referenced docs page";
            }

            var titleText = EvaluateLiquidText(rawTitle, liquidContext, version).Trim();
            return titleText.Length == 0 ? "the referenced docs page" : titleText;
        });
    }

    private static string RenderFrontmatterDiff(IReadOnlyList<MarkdownFrontmatterChange>? changes, bool hasVisibleBody)
    {
        if (changes is null || changes.Count == 0)
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        html.Append("<section class=\"rsr-frontmatter-diff\" data-testid=\"rsr-frontmatter-diff\" aria-label=\"フロントマター差分\">");
        html.Append("<h2>フロントマターの変更</h2>");
        html.Append("<p class=\"rsr-frontmatter-diff-overview\">")
            .Append(changes.Count.ToString(CultureInfo.InvariantCulture))
            .Append(hasVisibleBody
                ? " 件のメタデータ差分があります。本文の差分とあわせて確認してください。</p>"
                : " 件のメタデータ差分があります。本文がないため、レビュー対象は主にこの YAML です。</p>");
        html.Append("<ul class=\"rsr-frontmatter-diff-list\">");
        foreach (var change in changes.Take(12))
        {
            AppendFrontmatterChange(html, change);
        }
        if (changes.Count > 12)
        {
            html.Append("<li class=\"rsr-version-diff-more\">")
                .Append((changes.Count - 12).ToString(CultureInfo.InvariantCulture))
                .Append(" 件の追加の差分があります</li>");
        }
        html.Append("</ul></section>");
        return html.ToString();
    }

    private static void AppendFrontmatterChange(StringBuilder html, MarkdownFrontmatterChange change)
    {
        html.Append("<li class=\"rsr-frontmatter-change\" data-change-kind=\"")
            .Append(WebUtility.HtmlEncode(BuildChangeKindSlug(change.Kind)))
            .Append("\">");
        html.Append("<span class=\"rsr-frontmatter-change-kind\">")
            .Append(WebUtility.HtmlEncode(BuildChangeKindLabel(change.Kind)))
            .Append("</span>");
        if (!string.IsNullOrWhiteSpace(change.BeforeLine))
        {
            AppendFrontmatterLine(html, "変更前", change.BeforeLine);
        }
        if (!string.IsNullOrWhiteSpace(change.AfterLine))
        {
            AppendFrontmatterLine(html, "PR HEAD", change.AfterLine);
        }
        html.Append("</li>");
    }

    private static void AppendFrontmatterLine(StringBuilder html, string label, string line)
    {
        html.Append("<p class=\"rsr-frontmatter-line\"><span class=\"rsr-frontmatter-line-label\">")
            .Append(WebUtility.HtmlEncode(label))
            .Append("</span><code>")
            .Append(WebUtility.HtmlEncode(line))
            .Append("</code></p>");
    }

    private static List<VersionImpactGroup> BuildVersionImpactGroups(
        IReadOnlyList<DocsVersionImpactDetail> versionImpacts)
    {
        var builders = new List<VersionImpactGroupBuilder>();
        var lookup = new Dictionary<string, VersionImpactGroupBuilder>(StringComparer.Ordinal);
        for (var index = 0; index < versionImpacts.Count; index++)
        {
            var impact = versionImpacts[index];
            var key = BuildChangeSignature(impact.Changes);
            if (!lookup.TryGetValue(key, out var builder))
            {
                builder = new VersionImpactGroupBuilder(index, impact.Changes);
                lookup.Add(key, builder);
                builders.Add(builder);
            }
            builder.Versions.Add(impact.Version);
        }

        // 表示中の版を含むグループは呼び出し元で除外されるため、
        // 元の出現順 (FirstIndex) だけで安定ソートする。
        builders.Sort(static (left, right) => left.FirstIndex.CompareTo(right.FirstIndex));

        return builders
            .Select(static builder => new VersionImpactGroup(builder.Versions, builder.Changes))
            .ToList();
    }

    private static string BuildChangeSignature(IReadOnlyList<DocsVersionChangeSnippet> changes)
    {
        var signature = new StringBuilder();
        foreach (var change in changes)
        {
            AppendSignaturePart(signature, change.Kind.ToString());
            AppendSignaturePart(signature, change.BeforeExcerpt ?? string.Empty);
            AppendSignaturePart(signature, change.AfterExcerpt ?? string.Empty);
        }
        return signature.ToString();
    }

    private static void AppendSignaturePart(StringBuilder signature, string value)
    {
        signature.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');
    }

    private static string BuildVersionImpactGroupTitle(VersionImpactGroup group, int groupIndex)
        => group.Versions.Count == 1
            ? group.Versions[0].DisplayLabel + " のみ"
            : string.Create(CultureInfo.InvariantCulture, $"変更パターン {groupIndex + 1}: {group.Versions.Count} 版で同じ変更");

    private static void AppendVersionPatternBadges(
        StringBuilder html,
        IReadOnlyList<DocsVersion> versions)
    {
        // このヘルパーは currentVersion を含まない他版限定グループに対してのみ
        // 呼ばれるため、表示中バージョンのハイライトは生じない。
        html.Append("<ul class=\"rsr-version-pattern-versions\" aria-label=\"この変更が出る版\">");
        foreach (var version in versions)
        {
            html.Append("<li><button type=\"button\" class=\"rsr-version-pattern-badge\" data-rsr-version-slug=\"")
                .Append(WebUtility.HtmlEncode(version.Slug))
                .Append("\" data-version-slug=\"")
                .Append(WebUtility.HtmlEncode(version.Slug))
                .Append("\" aria-label=\"")
                .Append(WebUtility.HtmlEncode(version.DisplayLabel + " に切り替え"))
                .Append("\">")
                .Append(WebUtility.HtmlEncode(version.DisplayLabel))
                .Append("</button></li>");
        }
        html.Append("</ul>");
    }

    private static void AppendVersionChange(
        StringBuilder html,
        DocsVersionChangeSnippet change,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        html.Append("<div class=\"rsr-version-change\" data-change-kind=\"")
            .Append(WebUtility.HtmlEncode(BuildChangeKindSlug(change.Kind)))
            .Append("\">");
        html.Append("<span class=\"rsr-version-change-kind\">")
            .Append(WebUtility.HtmlEncode(BuildChangeKindLabel(change.Kind)))
            .Append("</span>");
        if (!string.IsNullOrWhiteSpace(change.BeforeExcerpt))
        {
            AppendVersionChangeExcerpt(
                html,
                "変更前",
                change.BeforeExcerpt,
                change.AfterExcerpt,
                VersionChangeExcerptSide.Before,
                repoPath,
                liquidContext,
                version);
        }
        if (!string.IsNullOrWhiteSpace(change.AfterExcerpt))
        {
            AppendVersionChangeExcerpt(
                html,
                "PR HEAD",
                change.AfterExcerpt,
                change.BeforeExcerpt,
                VersionChangeExcerptSide.After,
                repoPath,
                liquidContext,
                version);
        }
        html.Append("</div>");
    }

    private static void AppendVersionChangeExcerpt(
        StringBuilder html,
        string label,
        string excerpt,
        string? comparisonExcerpt,
        VersionChangeExcerptSide side,
        string repoPath,
        DocsLiquidContext liquidContext,
        DocsVersion version)
    {
        html.Append("<div class=\"rsr-version-change-line\"><span class=\"rsr-version-change-label\">")
            .Append(WebUtility.HtmlEncode(label))
            .Append("</span>");
        if (LooksLikeMarkdownTableExcerpt(excerpt))
        {
            html.Append("<p class=\"rsr-version-change-note\">表を含む差分です。本文のレンダリング済み差分で確認してください。</p>");
        }
        else
        {
            var displayExcerpt = RewriteAutotitlePlainTextLine(excerpt, repoPath, liquidContext, version);
            var displayComparisonExcerpt = comparisonExcerpt is null
                ? null
                : RewriteAutotitlePlainTextLine(comparisonExcerpt, repoPath, liquidContext, version);
            AppendVersionChangeExcerptText(html, displayExcerpt, displayComparisonExcerpt, side);
        }
        html.Append("</div>");
    }

    private static void AppendVersionChangeExcerptText(
        StringBuilder html,
        string excerpt,
        string? comparisonExcerpt,
        VersionChangeExcerptSide side)
    {
        html.Append("<span class=\"rsr-version-change-excerpt\">");
        var changedRange = FindInlineChangedRange(excerpt, comparisonExcerpt);
        if (changedRange.Length == 0)
        {
            html.Append(WebUtility.HtmlEncode(excerpt));
        }
        else
        {
            html.Append(WebUtility.HtmlEncode(excerpt[..changedRange.Start]));
            html.Append("<span class=\"")
                .Append(side == VersionChangeExcerptSide.Before
                    ? "rsr-version-change-excerpt--removed"
                    : "rsr-version-change-excerpt--added")
                .Append("\">")
                .Append(WebUtility.HtmlEncode(excerpt.Substring(changedRange.Start, changedRange.Length)))
                .Append("</span>");
            html.Append(WebUtility.HtmlEncode(excerpt[(changedRange.Start + changedRange.Length)..]));
        }
        html.Append("</span>");
    }

    private static InlineChangedRange FindInlineChangedRange(string excerpt, string? comparisonExcerpt)
    {
        if (excerpt.Length == 0 || string.Equals(excerpt, comparisonExcerpt, StringComparison.Ordinal))
        {
            return new InlineChangedRange(0, 0);
        }
        if (string.IsNullOrEmpty(comparisonExcerpt))
        {
            return new InlineChangedRange(0, excerpt.Length);
        }

        var prefixLength = 0;
        while (prefixLength < excerpt.Length
            && prefixLength < comparisonExcerpt.Length
            && excerpt[prefixLength] == comparisonExcerpt[prefixLength])
        {
            prefixLength++;
        }

        var excerptEnd = excerpt.Length - 1;
        var comparisonEnd = comparisonExcerpt.Length - 1;
        while (excerptEnd >= prefixLength
            && comparisonEnd >= prefixLength
            && excerpt[excerptEnd] == comparisonExcerpt[comparisonEnd])
        {
            excerptEnd--;
            comparisonEnd--;
        }

        return new InlineChangedRange(prefixLength, excerptEnd - prefixLength + 1);
    }

    private readonly record struct InlineChangedRange(int Start, int Length)
    {
        public int End => Start + Length;
    }

    private enum VersionChangeExcerptSide
    {
        Before,
        After,
    }

    private static string ApplyRenderedMarkdownDiff(
        string renderedMarkdown,
        string? diffAgainstMarkdown,
        DocsLiquidContext diffAgainstLiquidContext,
        string repoPath,
        string diffAgainstRepoPath,
        DocsLiquidContext currentLiquidContext,
        DocsVersion version,
        bool compareAutotitleLabels,
        RenderedMarkdownDiffSide diffSide)
    {
        if (diffSide == RenderedMarkdownDiffSide.None || string.IsNullOrEmpty(diffAgainstMarkdown))
        {
            return renderedMarkdown;
        }

        var (_, comparisonContent) = SplitFrontmatter(diffAgainstMarkdown);
        var comparisonRendered = DocsLiquidEvaluator.Evaluate(
            comparisonContent,
            diffAgainstLiquidContext,
            version,
            comparisonContext: currentLiquidContext);
        comparisonRendered = ExpandMarkdownTableFragments(comparisonRendered);
        if (compareAutotitleLabels)
        {
            comparisonRendered = RewriteAutotitleMarkdownLinks(
                comparisonRendered,
                diffAgainstRepoPath,
                diffAgainstLiquidContext,
                version);
        }
        if (string.Equals(renderedMarkdown, comparisonRendered, StringComparison.Ordinal))
        {
            return renderedMarkdown;
        }

        var currentLines = SplitMarkdownLines(renderedMarkdown);
        var comparisonLines = SplitMarkdownLines(comparisonRendered);
        var changes = FindCurrentLineDiffs(currentLines, comparisonLines);
        IncludeCodeFenceStructuralDiffBridges(currentLines, comparisonLines, changes);
        if (changes.Count == 0)
        {
            return renderedMarkdown;
        }
        var changesByIndex = changes.ToDictionary(static change => change.Index);

        var marked = new StringBuilder(renderedMarkdown.Length + (changes.Count * 48));
        var markerClass = diffSide == RenderedMarkdownDiffSide.After
            ? "rsr-rendered-diff-added"
            : "rsr-rendered-diff-removed";
        var inCodeFence = false;
        for (var index = 0; index < currentLines.Length; index++)
        {
            if (index > 0)
            {
                marked.Append('\n');
            }
            var line = currentLines[index];
            var trimmed = line.TrimStart();
            var isCodeFence = IsFenceLine(line);
            if (changesByIndex.TryGetValue(index, out var change))
            {
                if (inCodeFence && !isCodeFence)
                {
                    marked.Append(MarkRenderedDiffCodeLine(line, markerClass, change.ComparisonLines, comparisonLines));
                }
                else if (!inCodeFence && !isCodeFence && CanMarkRenderedDiffLine(trimmed))
                {
                    marked.Append(MarkRenderedDiffLine(
                        line,
                        markerClass,
                        change.ComparisonLines,
                        change.AlignedComparisonLine));
                }
                else
                {
                    marked.Append(line);
                }
            }
            else
            {
                marked.Append(line);
            }
            if (isCodeFence)
            {
                inCodeFence = !inCodeFence;
            }
        }
        return marked.ToString();
    }

    private static void IncludeCodeFenceStructuralDiffBridges(
        string[] currentLines,
        string[] comparisonLines,
        List<CurrentLineDiff> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var changedIndexes = changes.Select(static change => change.Index).ToHashSet();
        var inCodeFence = false;
        var fenceStart = 0;
        for (var index = 0; index < currentLines.Length; index++)
        {
            if (!IsFenceLine(currentLines[index]))
            {
                continue;
            }

            if (inCodeFence)
            {
                IncludeCodeFenceStructuralDiffBridges(currentLines, comparisonLines, fenceStart, index, changes, changedIndexes);
                inCodeFence = false;
            }
            else
            {
                inCodeFence = true;
                fenceStart = index + 1;
            }
        }
    }

    private static void IncludeCodeFenceStructuralDiffBridges(
        string[] currentLines,
        string[] comparisonLines,
        int startIndex,
        int endIndex,
        List<CurrentLineDiff> changes,
        HashSet<int> changedIndexes)
    {
        for (var index = startIndex; index < endIndex; index++)
        {
            if (!changedIndexes.Contains(index)
                || !TryFindChangedStructuralBlockEnd(currentLines, comparisonLines, index, endIndex, changedIndexes, out var blockEndIndex))
            {
                continue;
            }

            for (var bridgeIndex = index + 1; bridgeIndex < blockEndIndex; bridgeIndex++)
            {
                SetCodeFenceStructuralBridgeDiff(currentLines, changes, changedIndexes, bridgeIndex);
            }
        }
    }

    private static void SetCodeFenceStructuralBridgeDiff(
        string[] currentLines,
        List<CurrentLineDiff> changes,
        HashSet<int> changedIndexes,
        int index)
    {
        if (string.IsNullOrWhiteSpace(currentLines[index]))
        {
            return;
        }

        var bridged = new CurrentLineDiff(index, Array.Empty<string>(), null);
        var existingIndex = changes.FindIndex(change => change.Index == index);
        if (existingIndex >= 0)
        {
            changes[existingIndex] = bridged;
            return;
        }

        changedIndexes.Add(index);
        changes.Add(bridged);
    }

    private static bool TryFindChangedStructuralBlockEnd(
        string[] lines,
        string[] comparisonLines,
        int startIndex,
        int endIndex,
        HashSet<int> changedIndexes,
        out int blockEndIndex)
    {
        blockEndIndex = -1;
        if (!IsNamedCodeStructuralBlockOpeningLine(lines[startIndex]))
        {
            return false;
        }
        if (FindExactTrimmedCodeLineMatch(lines[startIndex], comparisonLines) is not null)
        {
            return false;
        }

        var depth = CountCodeStructuralBracketDelta(lines[startIndex]);
        if (depth <= 0)
        {
            return false;
        }

        for (var index = startIndex + 1; index < endIndex; index++)
        {
            depth += CountCodeStructuralBracketDelta(lines[index]);
            if (depth <= 0)
            {
                blockEndIndex = index;
                if (HasMatchingCodeStructuralBlockBody(lines, startIndex, blockEndIndex, comparisonLines))
                {
                    return false;
                }

                return changedIndexes.Contains(blockEndIndex);
            }
        }

        return false;
    }

    private static bool IsNamedCodeStructuralBlockOpeningLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Contains(':', StringComparison.Ordinal)
            && (trimmed.EndsWith('[', StringComparison.Ordinal) || trimmed.EndsWith('{', StringComparison.Ordinal));
    }

    private static bool HasMatchingCodeStructuralBlockBody(
        string[] currentLines,
        int currentStartIndex,
        int currentEndIndex,
        string[] comparisonLines)
    {
        var openingKind = GetCodeStructuralOpeningKind(currentLines[currentStartIndex]);
        for (var index = 0; index < comparisonLines.Length; index++)
        {
            if (!IsNamedCodeStructuralBlockOpeningLine(comparisonLines[index])
                || GetCodeStructuralOpeningKind(comparisonLines[index]) != openingKind
                || !TryFindCodeStructuralBlockEnd(comparisonLines, index, comparisonLines.Length, out var comparisonEndIndex))
            {
                continue;
            }

            if (CodeStructuralBlockBodiesMatch(currentLines, currentStartIndex, currentEndIndex, comparisonLines, index, comparisonEndIndex))
            {
                return true;
            }
        }

        return false;
    }

    private static char GetCodeStructuralOpeningKind(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.EndsWith('[', StringComparison.Ordinal) ? '[' : '{';
    }

    private static bool TryFindCodeStructuralBlockEnd(string[] lines, int startIndex, int endIndex, out int blockEndIndex)
    {
        blockEndIndex = -1;
        var depth = CountCodeStructuralBracketDelta(lines[startIndex]);
        if (depth <= 0)
        {
            return false;
        }

        for (var index = startIndex + 1; index < endIndex; index++)
        {
            depth += CountCodeStructuralBracketDelta(lines[index]);
            if (depth <= 0)
            {
                blockEndIndex = index;
                return true;
            }
        }

        return false;
    }

    private static bool CodeStructuralBlockBodiesMatch(
        string[] leftLines,
        int leftStartIndex,
        int leftEndIndex,
        string[] rightLines,
        int rightStartIndex,
        int rightEndIndex)
    {
        var leftLength = leftEndIndex - leftStartIndex - 1;
        if (leftLength != rightEndIndex - rightStartIndex - 1)
        {
            return false;
        }

        for (var offset = 0; offset < leftLength; offset++)
        {
            if (!string.Equals(
                    leftLines[leftStartIndex + 1 + offset].Trim(),
                    rightLines[rightStartIndex + 1 + offset].Trim(),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountCodeStructuralBracketDelta(string line)
    {
        var depth = 0;
        var inString = false;
        var escaping = false;
        foreach (var ch in line)
        {
            if (escaping)
            {
                escaping = false;
                continue;
            }

            if (inString && ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            depth += ch is '[' or '{' ? 1 : 0;
            depth -= ch is ']' or '}' ? 1 : 0;
        }

        return depth;
    }

    private static string ExpandMarkdownTableFragments(
        string markdown,
        CancellationToken cancellationToken = default)
    {
        var normalizedMarkdown = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalizedMarkdown.Split('\n');
        var expanded = new StringBuilder(markdown.Length + 128);
        var pendingRows = new List<string>();
        var protectedLines = FindProtectedMarkdownBlockLines(
            normalizedMarkdown,
            lines.Length,
            cancellationToken);
        for (var index = 0; index < lines.Length; index++)
        {
            if ((index & 127) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var line = lines[index];
            if (protectedLines[index])
            {
                FlushTableFragment(expanded, pendingRows);
                if (expanded.Length > 0)
                {
                    expanded.Append('\n');
                }
                expanded.Append(line);
                continue;
            }

            var tableRows = SplitConcatenatedMarkdownTableRows(line);
            if (tableRows.Count > 0)
            {
                pendingRows.AddRange(tableRows);
                continue;
            }

            FlushTableFragment(expanded, pendingRows);
            if (expanded.Length > 0)
            {
                expanded.Append('\n');
            }
            expanded.Append(line);
        }

        FlushTableFragment(expanded, pendingRows);
        return expanded.ToString();
    }

    private static bool[] FindProtectedMarkdownBlockLines(
        string markdown,
        int lineCount,
        CancellationToken cancellationToken)
    {
        var protectedLines = new bool[lineCount];
        var lineStarts = new int[lineCount];
        var lineIndex = 1;
        for (var offset = 0; offset < markdown.Length && lineIndex < lineCount; offset++)
        {
            if ((offset & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (markdown[offset] == '\n')
            {
                lineStarts[lineIndex++] = offset + 1;
            }
        }

        var document = Markdown.Parse(markdown, _pipeline);
        foreach (var block in document.Descendants().OfType<Block>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block is not (CodeBlock or HtmlBlock) || block.Span.Start < 0 || block.Span.End < block.Span.Start)
            {
                continue;
            }

            var startLine = FindSourceLine(lineStarts, block.Span.Start);
            var endLine = FindSourceLine(lineStarts, block.Span.End);
            Array.Fill(protectedLines, true, startLine, endLine - startLine + 1);
        }

        return protectedLines;
    }

    private static int FindSourceLine(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    private static List<string> SplitConcatenatedMarkdownTableRows(string line)
    {
        if (IsMarkdownTableLine(line))
        {
            return [line];
        }

        var trimmed = line.Trim();
        if (!trimmed.Contains("||", StringComparison.Ordinal))
        {
            return [];
        }

        var rows = new List<string>();
        foreach (var part in trimmed.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = part.Trim('|', ' ');
            if (!IsMarkdownTableLine(candidate))
            {
                return [];
            }
            rows.Add(candidate);
        }
        return rows.Count > 1 ? rows : [];
    }

    private static void FlushTableFragment(StringBuilder markdown, List<string> pendingRows)
    {
        if (pendingRows.Count == 0)
        {
            return;
        }

        var columnCount = pendingRows.Select(static row => SplitMarkdownTableRow(row).Count).DefaultIfEmpty(0).Max();
        if (columnCount > 0 && !pendingRows.Any(IsMarkdownTableSeparatorRow))
        {
            if (markdown.Length > 0)
            {
                markdown.Append('\n');
            }
            AppendInferredTableHeader(markdown, columnCount);
        }

        for (var index = 0; index < pendingRows.Count; index++)
        {
            if (markdown.Length > 0)
            {
                markdown.Append('\n');
            }
            var row = pendingRows[index];
            markdown.Append(HasCompatibleMarkdownTableHeader(pendingRows, index)
                ? NormalizeMarkdownTableSeparatorRow(row)
                : row);
        }
        pendingRows.Clear();
    }

    private static bool HasCompatibleMarkdownTableHeader(List<string> rows, int separatorIndex)
    {
        if (separatorIndex != 1 || !IsMarkdownTableSeparatorRow(rows[separatorIndex]))
        {
            return false;
        }

        var headerColumnCount = SplitMarkdownTableRow(rows[separatorIndex - 1]).Count;
        return headerColumnCount >= 2
            && headerColumnCount == CountMarkdownTableSeparatorColumns(rows[separatorIndex]);
    }

    private static int CountMarkdownTableSeparatorColumns(string value)
    {
        var trimmed = value.Trim();
        var cells = trimmed.Split('|');
        var firstCell = trimmed.StartsWith('|') ? 1 : 0;
        var lastCell = cells.Length - (trimmed.EndsWith('|') ? 1 : 0);
        return Math.Max(0, lastCell - firstCell);
    }

    private static void AppendInferredTableHeader(StringBuilder markdown, int columnCount)
    {
        var headers = columnCount switch
        {
            2 => ["Name", "Description"],
            3 => ["Name", "Type", "Description"],
            _ => Enumerable.Range(1, columnCount).Select(static index => string.Create(CultureInfo.InvariantCulture, $"Column {index}")).ToArray(),
        };
        markdown.Append("| ").AppendJoin(" | ", headers).AppendLine(" |");
        markdown.Append('|');
        for (var index = 0; index < columnCount; index++)
        {
            markdown.Append(" --- |");
        }
    }

    private static bool CanMarkRenderedDiffLine(string trimmedLine)
        => !trimmedLine.StartsWith("{%", StringComparison.Ordinal)
            && !trimmedLine.StartsWith("{{", StringComparison.Ordinal)
            && !trimmedLine.StartsWith('<');

    private static string[] SplitMarkdownLines(string markdown)
        => markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static List<CurrentLineDiff> FindCurrentLineDiffs(string[] currentLines, string[] comparisonLines)
    {
        var table = BuildLineLcsTable(currentLines, comparisonLines);
        var changed = new List<CurrentLineDiff>();
        var currentHunkIndexes = new List<int>();
        var comparisonHunkLines = new List<string>();
        var currentIndex = 0;
        var comparisonIndex = 0;
        while (currentIndex < currentLines.Length || comparisonIndex < comparisonLines.Length)
        {
            if (currentIndex < currentLines.Length
                && comparisonIndex < comparisonLines.Length
                && string.Equals(currentLines[currentIndex], comparisonLines[comparisonIndex], StringComparison.Ordinal))
            {
                FlushCurrentLineDiffs(changed, currentHunkIndexes, comparisonHunkLines);
                currentIndex++;
                comparisonIndex++;
            }
            else if (currentIndex < currentLines.Length
                && (comparisonIndex == comparisonLines.Length || table[currentIndex + 1, comparisonIndex] >= table[currentIndex, comparisonIndex + 1]))
            {
                if (!string.IsNullOrWhiteSpace(currentLines[currentIndex]))
                {
                    currentHunkIndexes.Add(currentIndex);
                }
                currentIndex++;
            }
            else
            {
                if (comparisonIndex < comparisonLines.Length && !string.IsNullOrWhiteSpace(comparisonLines[comparisonIndex]))
                {
                    comparisonHunkLines.Add(comparisonLines[comparisonIndex]);
                }
                comparisonIndex++;
            }
        }
        FlushCurrentLineDiffs(changed, currentHunkIndexes, comparisonHunkLines);
        return changed;
    }

    private static void FlushCurrentLineDiffs(
        List<CurrentLineDiff> changed,
        List<int> currentHunkIndexes,
        List<string> comparisonHunkLines)
    {
        if (currentHunkIndexes.Count == 0)
        {
            comparisonHunkLines.Clear();
            return;
        }

        var candidates = comparisonHunkLines.ToArray();
        var hasPositionalAlignment = currentHunkIndexes.Count == candidates.Length;
        for (var position = 0; position < currentHunkIndexes.Count; position++)
        {
            changed.Add(new CurrentLineDiff(
                currentHunkIndexes[position],
                candidates,
                hasPositionalAlignment ? candidates[position] : null));
        }
        currentHunkIndexes.Clear();
        comparisonHunkLines.Clear();
    }

    private static int[,] BuildLineLcsTable(string[] currentLines, string[] comparisonLines)
    {
        var table = new int[currentLines.Length + 1, comparisonLines.Length + 1];
        for (var currentIndex = currentLines.Length - 1; currentIndex >= 0; currentIndex--)
        {
            for (var comparisonIndex = comparisonLines.Length - 1; comparisonIndex >= 0; comparisonIndex--)
            {
                table[currentIndex, comparisonIndex] = string.Equals(currentLines[currentIndex], comparisonLines[comparisonIndex], StringComparison.Ordinal)
                    ? table[currentIndex + 1, comparisonIndex + 1] + 1
                    : Math.Max(table[currentIndex + 1, comparisonIndex], table[currentIndex, comparisonIndex + 1]);
            }
        }
        return table;
    }

    private static string MarkRenderedDiffLine(
        string line,
        string markerClass,
        string[] comparisonLines,
        string? alignedComparisonLine)
    {
        if (IsMarkdownTableSeparatorRow(line))
        {
            return line;
        }
        if (IsMarkdownTableLine(line))
        {
            return MarkMarkdownTableLine(line, markerClass, FindComparableMarkdownTableLine(line, comparisonLines));
        }

        if (TryGetMarkableRenderedDiffParts(line, out var parts))
        {
            var comparisonContent = FindComparableRenderedDiffContent(
                parts,
                comparisonLines,
                alignedComparisonLine);
            return parts.Prefix + MarkRenderedDiffContent(parts.Content, markerClass, comparisonContent);
        }

        return line;
    }

    private static string MarkRenderedDiffCodeLine(
        string line,
        string markerClass,
        string[] comparisonLines,
        string[] allComparisonLines)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        var comparisonLine = FindComparableRenderedDiffCodeLine(line, comparisonLines);
        comparisonLine ??= comparisonLines.Length == 0
            ? null
            : FindExactTrimmedCodeLineMatch(line, allComparisonLines);
        var leadingWhitespaceLength = CountLeadingWhitespace(line);
        var prefix = line[..leadingWhitespaceLength];
        var content = line[leadingWhitespaceLength..];
        var comparisonContent = comparisonLine is null
            ? null
            : comparisonLine[CountLeadingWhitespace(comparisonLine)..];
        return prefix + MarkRenderedDiffCodeContent(content, markerClass, comparisonContent);
    }

    private static string MarkRenderedDiffCodeContent(string content, string markerClass, string? comparisonContent)
    {
        if (string.IsNullOrEmpty(comparisonContent))
        {
            return MarkdownCodeDiffMarker.Wrap(content, markerClass);
        }
        if (IsWhitespaceOnlyCodeLineDiff(content, comparisonContent))
        {
            return content;
        }

        var changedRange = FindInlineChangedRange(content, comparisonContent);
        if (changedRange.Length == 0)
        {
            return TryMarkRenderedDiffCodeGap(content, comparisonContent, markerClass, changedRange.Start, out var marked)
                ? marked
                : content;
        }

        return content[..changedRange.Start]
            + MarkdownCodeDiffMarker.Wrap(content.Substring(changedRange.Start, changedRange.Length), markerClass)
            + content[(changedRange.Start + changedRange.Length)..];
    }

    private static bool TryMarkRenderedDiffCodeGap(
        string content,
        string comparisonContent,
        string markerClass,
        int insertionIndex,
        out string marked)
    {
        marked = content;
        if (content.Length >= comparisonContent.Length
            || insertionIndex < 0
            || insertionIndex > content.Length)
        {
            return false;
        }

        var gapClass = string.Equals(markerClass, "rsr-rendered-diff-added", StringComparison.Ordinal)
            ? "rsr-rendered-diff-removed"
            : "rsr-rendered-diff-added";
        marked = content[..insertionIndex]
            + MarkdownCodeDiffMarker.CreateGap(gapClass + " rsr-rendered-diff-gap")
            + content[insertionIndex..];
        return true;
    }

    private static string? FindComparableRenderedDiffCodeLine(string line, string[] comparisonLines)
    {
        var trimmedLine = line.Trim();
        foreach (var comparisonLine in comparisonLines)
        {
            if (!IsFenceLine(comparisonLine)
                && string.Equals(trimmedLine, comparisonLine.Trim(), StringComparison.Ordinal))
            {
                return comparisonLine;
            }
        }

        string? bestLine = null;
        var bestScore = 0;
        foreach (var comparisonLine in comparisonLines)
        {
            if (IsFenceLine(comparisonLine))
            {
                continue;
            }

            var changedRange = FindInlineChangedRange(line, comparisonLine);
            var score = line.Length - changedRange.Length;
            if (score > bestScore)
            {
                bestScore = score;
                bestLine = comparisonLine;
            }
        }

        var minimumScore = Math.Max(4, line.Length / 3);
        return bestScore >= minimumScore && bestScore * 5 >= line.Length * 3
            ? bestLine
            : null;
    }

    private static string? FindExactTrimmedCodeLineMatch(string line, string[] comparisonLines)
    {
        var trimmedLine = line.Trim();
        foreach (var comparisonLine in comparisonLines)
        {
            if (!IsFenceLine(comparisonLine)
                && string.Equals(trimmedLine, comparisonLine.Trim(), StringComparison.Ordinal))
            {
                return comparisonLine;
            }
        }

        return null;
    }

    private static bool TryGetMarkableRenderedDiffParts(string line, out RenderedDiffLineParts parts)
    {
        var trimmedStartLength = line.Length - line.TrimStart().Length;
        var leading = line[..trimmedStartLength];
        var content = line[trimmedStartLength..];
        if (content.StartsWith("> ", StringComparison.Ordinal)
            || content.StartsWith(">[!", StringComparison.Ordinal))
        {
            var quotePrefixLength = content.Length > 1 && content[1] == ' ' ? 2 : 1;
            var quotePrefix = content[..quotePrefixLength];
            var quoted = content[quotePrefixLength..];
            if (TrySplitGitHubAlertQuotedLine(quoted, out var alertMarkerPrefix, out var alertInlineContent))
            {
                if (alertInlineContent.Length == 0)
                {
                    parts = default;
                    return false;
                }

                parts = new RenderedDiffLineParts(
                    RenderedDiffLineKind.Quote,
                    leading + quotePrefix + alertMarkerPrefix,
                    alertInlineContent);
                return true;
            }
            if (IsGitHubAlertMarker(quoted) || string.IsNullOrWhiteSpace(quoted))
            {
                parts = default;
                return false;
            }
            var quotedListMarkerLength = GetMarkdownListMarkerLength(quoted);
            if (quotedListMarkerLength > 0)
            {
                parts = new RenderedDiffLineParts(
                    RenderedDiffLineKind.QuoteListItem,
                    leading + quotePrefix + quoted[..quotedListMarkerLength],
                    quoted[quotedListMarkerLength..]);
                return true;
            }
            parts = new RenderedDiffLineParts(RenderedDiffLineKind.Quote, leading + quotePrefix, quoted);
            return true;
        }
        if (string.Equals(content, ">", StringComparison.Ordinal))
        {
            parts = default;
            return false;
        }

        var footnoteMarkerLength = GetMarkdownFootnoteDefinitionMarkerLength(content);
        if (footnoteMarkerLength > 0)
        {
            parts = new RenderedDiffLineParts(
                RenderedDiffLineKind.FootnoteDefinition,
                leading + content[..footnoteMarkerLength],
                content[footnoteMarkerLength..]);
            return true;
        }

        var headingMarkerLength = GetMarkdownHeadingMarkerLength(content);
        if (headingMarkerLength > 0)
        {
            parts = new RenderedDiffLineParts(
                RenderedDiffLineKind.Heading,
                leading + content[..headingMarkerLength] + " ",
                content[(headingMarkerLength + 1)..]);
            return true;
        }
        var listMarkerLength = GetMarkdownListMarkerLength(content);
        if (listMarkerLength > 0)
        {
            parts = new RenderedDiffLineParts(
                RenderedDiffLineKind.ListItem,
                leading + content[..listMarkerLength],
                content[listMarkerLength..]);
            return true;
        }
        parts = new RenderedDiffLineParts(RenderedDiffLineKind.Paragraph, leading, content);
        return true;
    }

    private static int GetMarkdownFootnoteDefinitionMarkerLength(string content)
    {
        if (!content.StartsWith("[^", StringComparison.Ordinal))
        {
            return 0;
        }

        var markerEnd = content.IndexOf("]:", 2, StringComparison.Ordinal);
        if (markerEnd <= 2)
        {
            return 0;
        }

        var contentStart = markerEnd + 2;
        while (contentStart < content.Length && char.IsWhiteSpace(content[contentStart]))
        {
            contentStart++;
        }
        return contentStart;
    }

    private static int GetMarkdownListMarkerLength(string content)
    {
        if (content.Length >= 2
            && (content[0] is '-' or '*' or '+')
            && char.IsWhiteSpace(content[1]))
        {
            return 2;
        }

        var digitLength = 0;
        while (digitLength < content.Length && char.IsDigit(content[digitLength]))
        {
            digitLength++;
        }
        if (digitLength > 0
            && digitLength + 1 < content.Length
            && content[digitLength] is '.' or ')'
            && char.IsWhiteSpace(content[digitLength + 1]))
        {
            return digitLength + 2;
        }

        return 0;
    }

    private static string MarkRenderedDiffContent(string content, string markerClass, string? comparisonContent)
    {
        if (string.IsNullOrEmpty(comparisonContent))
        {
            return "<span class=\"" + markerClass + "\">" + content + "</span>";
        }

        var changedRange = FindInlineChangedRange(content, comparisonContent);
        if (changedRange.Length == 0)
        {
            return TryMarkRenderedDiffGap(content, comparisonContent, markerClass, changedRange.Start, out var marked)
                ? marked
                : content;
        }
        changedRange = ExpandRenderedDiffRange(content, changedRange);
        changedRange = ExpandRenderedDiffRangeAroundChangedAnchor(content, changedRange);
        changedRange = SnapRangeOutsideLiquidTokens(content, changedRange);
        if (changedRange.Length == 0)
        {
            return content;
        }

        return WrapRenderedDiffRangePreservingInlineCode(content, markerClass, changedRange);
    }

    private static InlineChangedRange ExpandRenderedDiffRangeAroundChangedAnchor(
        string content,
        InlineChangedRange changedRange)
    {
        var changedEnd = changedRange.End;
        var searchStart = 0;
        while (searchStart < content.Length)
        {
            var anchorStart = content.IndexOf("<a", searchStart, StringComparison.OrdinalIgnoreCase);
            if (anchorStart < 0)
            {
                return changedRange;
            }

            var openingEnd = content.IndexOf('>', anchorStart + 2);
            if (openingEnd < 0)
            {
                return changedRange;
            }

            var closingStart = content.IndexOf("</a>", openingEnd + 1, StringComparison.OrdinalIgnoreCase);
            if (closingStart < 0)
            {
                return changedRange;
            }

            var closingEnd = closingStart + 4;
            var changedRangeIntersectsOpeningTag = changedRange.Start < openingEnd + 1
                && changedEnd > anchorStart;
            if (changedRangeIntersectsOpeningTag)
            {
                var expandedStart = Math.Min(changedRange.Start, anchorStart);
                var expandedEnd = Math.Max(changedEnd, closingEnd);
                return new InlineChangedRange(expandedStart, expandedEnd - expandedStart);
            }

            searchStart = closingEnd;
        }

        return changedRange;
    }

    private static string WrapRenderedDiffRangePreservingInlineCode(
        string content,
        string markerClass,
        InlineChangedRange changedRange)
    {
        var marked = new StringBuilder(content.Length + 96);
        marked.Append(content.AsSpan(0, changedRange.Start));
        var cursor = changedRange.Start;
        while (cursor < changedRange.End)
        {
            var tickStart = content.IndexOf('`', cursor, changedRange.End - cursor);
            if (tickStart < 0)
            {
                marked.Append(WrapRenderedDiff(content[cursor..changedRange.End], markerClass));
                break;
            }

            marked.Append(WrapRenderedDiff(content[cursor..tickStart], markerClass));
            var tickCount = 1;
            while (tickStart + tickCount < changedRange.End
                && content[tickStart + tickCount] == '`')
            {
                tickCount++;
            }

            var tickEnd = FindInlineCodeEnd(content, tickStart);
            if (tickEnd < 0 || tickEnd > changedRange.End)
            {
                marked.Append(content.AsSpan(tickStart, tickCount));
                cursor = tickStart + tickCount;
                continue;
            }

            var codeEnd = tickEnd - tickCount;
            marked.Append(content.AsSpan(tickStart, tickCount))
                .Append(WrapRenderedDiff(
                    content.Substring(tickStart + tickCount, codeEnd - tickStart - tickCount),
                    markerClass))
                .Append(content.AsSpan(codeEnd, tickCount));
            cursor = tickEnd;
        }
        marked.Append(content.AsSpan(changedRange.End));
        return marked.ToString();
    }

    private static bool TryMarkRenderedDiffGap(
        string content,
        string comparisonContent,
        string markerClass,
        int insertionIndex,
        out string marked)
    {
        marked = content;
        if (content.Length >= comparisonContent.Length
            || insertionIndex < 0
            || insertionIndex > content.Length)
        {
            return false;
        }

        var gapClass = string.Equals(markerClass, "rsr-rendered-diff-added", StringComparison.Ordinal)
            ? "rsr-rendered-diff-removed"
            : "rsr-rendered-diff-added";
        var marker = "<span class=\"" + gapClass + " rsr-rendered-diff-gap\" aria-hidden=\"true\"></span>";
        marked = content[..insertionIndex] + marker + content[insertionIndex..];
        return true;
    }

    private static InlineChangedRange ExpandRenderedDiffRange(string content, InlineChangedRange changedRange)
    {
        if (changedRange.Length == 0
            || !TryFindMarkdownLinkAroundRange(content, changedRange, out var linkRange))
        {
            return changedRange;
        }
        var start = FindSentenceStart(content, linkRange.LabelStart);
        var sentencePrefix = content[start..linkRange.LabelStart].TrimStart();
        if (sentencePrefix.StartsWith("For more information", StringComparison.OrdinalIgnoreCase)
            || sentencePrefix.StartsWith("For more info", StringComparison.OrdinalIgnoreCase))
        {
            start = FindPreviousSentenceStart(content, start);
        }

        var end = FindSentenceEnd(content, linkRange.LinkEnd);
        return end > start ? new InlineChangedRange(start, end - start) : changedRange;
    }

    /// <summary>
    /// 差分マーカー span が Liquid タグ (<c>{% ... %}</c> / <c>{{ ... }}</c>) を
    /// 途中で分断しないよう、範囲の端がタグ内部に入っている場合はタグの外側へ
    /// スナップする。タグ内に span を挿入すると後段の Liquid 中立化で壊れる
    /// (タグ崩れ・HTML エスケープ混入・見出し id 破損) のを防ぐ。
    /// </summary>
    private static InlineChangedRange SnapRangeOutsideLiquidTokens(string content, InlineChangedRange range)
    {
        if (range.Length == 0)
        {
            return range;
        }

        var start = range.Start;
        var end = range.Start + range.Length;
        foreach (var token in EnumerateLiquidTokenSpans(content))
        {
            // 開始端がタグ内部 (開きより後ろ・閉じより前) ならタグ先頭へ。
            if (start > token.Start && start < token.End)
            {
                start = token.Start;
            }
            // 終了端がタグ内部ならタグ末尾へ。
            if (end > token.Start && end < token.End)
            {
                end = token.End;
            }
        }

        start = Math.Clamp(start, 0, content.Length);
        end = Math.Clamp(end, start, content.Length);
        return new InlineChangedRange(start, end - start);
    }

    private static IEnumerable<InlineChangedRange> EnumerateLiquidTokenSpans(string content)
    {
        var cursor = 0;
        while (cursor < content.Length - 1)
        {
            var open = -1;
            string? closing = null;
            for (var index = cursor; index < content.Length - 1; index++)
            {
                if (content[index] == '{' && (content[index + 1] == '%' || content[index + 1] == '{'))
                {
                    open = index;
                    closing = content[index + 1] == '%' ? "%}" : "}}";
                    break;
                }
            }
            if (open < 0 || closing is null)
            {
                yield break;
            }

            var closeIndex = content.IndexOf(closing, open + 2, StringComparison.Ordinal);
            if (closeIndex < 0)
            {
                yield break;
            }

            var endExclusive = closeIndex + 2;
            yield return new InlineChangedRange(open, endExclusive - open);
            cursor = endExclusive;
        }
    }

    private static bool TryFindMarkdownLinkAroundRange(
        string content,
        InlineChangedRange changedRange,
        out MarkdownLinkRange linkRange)
    {
        linkRange = default;
        var changedStart = changedRange.Start;
        var changedEnd = changedRange.Start + changedRange.Length;
        var searchStart = 0;
        while (searchStart < content.Length)
        {
            var labelEnd = content.IndexOf("](", searchStart, StringComparison.Ordinal);
            if (labelEnd < 0)
            {
                return false;
            }

            var labelStart = content.LastIndexOf('[', labelEnd);
            if (labelStart < 0 || ContainsLineBreak(content, labelStart, labelEnd))
            {
                searchStart = labelEnd + 2;
                continue;
            }

            var nestedLabelEnd = content.IndexOf(']', labelStart, labelEnd - labelStart);
            if (nestedLabelEnd >= 0)
            {
                searchStart = labelEnd + 2;
                continue;
            }

            var linkEnd = content.IndexOf(')', labelEnd + 2);
            if (linkEnd < 0)
            {
                return false;
            }

            if (changedEnd > labelStart && changedStart < linkEnd + 1)
            {
                linkRange = new MarkdownLinkRange(labelStart, labelEnd, linkEnd + 1);
                return true;
            }

            searchStart = labelEnd + 2;
        }

        return false;
    }

    private static bool ContainsLineBreak(string value, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (value[index] is '\r' or '\n')
            {
                return true;
            }
        }
        return false;
    }

    private static int FindSentenceStart(string content, int index)
    {
        var cursor = Math.Min(index, content.Length);
        while (cursor > 0)
        {
            var previous = content[cursor - 1];
            if (previous is '.' or '!' or '?')
            {
                return SkipSentenceBoundaryWhitespace(content, cursor);
            }
            cursor--;
        }
        return 0;
    }

    private static int FindPreviousSentenceStart(string content, int sentenceStart)
    {
        var cursor = Math.Max(0, sentenceStart - 1);
        while (cursor > 0 && char.IsWhiteSpace(content[cursor - 1]))
        {
            cursor--;
        }
        if (cursor > 0 && content[cursor - 1] is '.' or '!' or '?')
        {
            cursor--;
        }
        while (cursor > 0)
        {
            var previous = content[cursor - 1];
            if (previous is '.' or '!' or '?')
            {
                return SkipSentenceBoundaryWhitespace(content, cursor);
            }
            cursor--;
        }
        return 0;
    }

    private static int FindSentenceEnd(string content, int index)
    {
        var cursor = Math.Min(index, content.Length);
        while (cursor < content.Length)
        {
            var current = content[cursor];
            if (current is '.' or '!' or '?')
            {
                return cursor + 1;
            }
            cursor++;
        }
        return content.Length;
    }

    private static int SkipSentenceBoundaryWhitespace(string content, int index)
    {
        var cursor = index;
        while (cursor < content.Length && char.IsWhiteSpace(content[cursor]))
        {
            cursor++;
        }
        return cursor;
    }

    private static string? FindComparableRenderedDiffContent(
        RenderedDiffLineParts currentParts,
        string[] comparisonLines,
        string? alignedComparisonLine)
    {
        string? alignedContent = null;
        if (alignedComparisonLine is not null
            && TryGetMarkableRenderedDiffParts(alignedComparisonLine, out var alignedParts)
            && alignedParts.Kind == currentParts.Kind
            && HasMeaningfulAlignedSimilarity(currentParts.Content, alignedParts.Content))
        {
            alignedContent = alignedParts.Content;
        }

        string? prefixOrSuffixMatch = null;
        var prefixOrSuffixScore = 0;
        string? bestContent = null;
        var bestScore = 0;
        foreach (var comparisonLine in comparisonLines)
        {
            if (!TryGetMarkableRenderedDiffParts(comparisonLine, out var comparisonParts)
                || comparisonParts.Kind != currentParts.Kind)
            {
                continue;
            }

            if (currentParts.Content.StartsWith(comparisonParts.Content, StringComparison.Ordinal)
                || currentParts.Content.EndsWith(comparisonParts.Content, StringComparison.Ordinal)
                || comparisonParts.Content.StartsWith(currentParts.Content, StringComparison.Ordinal)
                || comparisonParts.Content.EndsWith(currentParts.Content, StringComparison.Ordinal))
            {
                if (comparisonParts.Content.Length > prefixOrSuffixScore)
                {
                    prefixOrSuffixScore = comparisonParts.Content.Length;
                    prefixOrSuffixMatch = comparisonParts.Content;
                }
                continue;
            }

            var changedRange = FindInlineChangedRange(currentParts.Content, comparisonParts.Content);
            var score = currentParts.Content.Length - changedRange.Length;
            if (score > bestScore)
            {
                bestScore = score;
                bestContent = comparisonParts.Content;
            }
        }

        if (prefixOrSuffixMatch is not null)
        {
            return prefixOrSuffixMatch;
        }

        // Avoid treating a genuinely added line as an update just because it
        // shares a short word or phrase with an unrelated existing line (for
        // example "Agent apps" vs "GitHub Apps and OAuth apps", or two
        // unrelated sentences that both start with "If the app"). Require both
        // a small absolute overlap and a meaningful ratio so short real updates
        // like "Old entry" -> "New entry" still get inline marking.
        var minimumScore = Math.Max(4, currentParts.Content.Length / 3);
        if (bestScore >= minimumScore && bestScore * 5 >= currentParts.Content.Length * 3)
        {
            return bestContent;
        }

        return alignedContent;
    }

    private static bool HasMeaningfulAlignedSimilarity(string currentContent, string comparisonContent)
    {
        var currentTokens = SimilarityTokenRegex()
            .Matches(currentContent)
            .Select(static match => match.Value)
            .Where(IsSignificantSimilarityToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var comparisonTokens = SimilarityTokenRegex()
            .Matches(comparisonContent)
            .Select(static match => match.Value)
            .Where(IsSignificantSimilarityToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shorterCount = Math.Min(currentTokens.Count, comparisonTokens.Count);
        if (shorterCount == 0)
        {
            return false;
        }

        var sharedCount = currentTokens.Intersect(comparisonTokens).Count();
        return sharedCount >= 4 && sharedCount * 2 >= shorterCount;
    }

    private static bool IsSignificantSimilarityToken(string token)
        => token.Length >= 4 && !_similarityStopWords.Contains(token);

    private static bool IsWhitespaceOnlyCodeLineDiff(string content, string comparisonContent)
        => !string.Equals(content, comparisonContent, StringComparison.Ordinal)
            && string.Equals(content.Trim(), comparisonContent.Trim(), StringComparison.Ordinal);

    private static int CountLeadingWhitespace(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }
        return index;
    }

    private readonly record struct RenderedDiffLineParts(RenderedDiffLineKind Kind, string Prefix, string Content);

    private readonly record struct CurrentLineDiff(
        int Index,
        string[] ComparisonLines,
        string? AlignedComparisonLine);

    private readonly record struct MarkdownLinkRange(int LabelStart, int LabelEnd, int LinkEnd);

    private enum RenderedDiffLineKind
    {
        Paragraph,
        Heading,
        ListItem,
        QuoteListItem,
        Quote,
        FootnoteDefinition,
    }

    private static bool IsGitHubAlertMarker(string value)
        => value.TrimStart().StartsWith("[!", StringComparison.Ordinal);

    private static bool TrySplitGitHubAlertQuotedLine(
        string quoted,
        out string markerPrefix,
        out string inlineContent)
    {
        markerPrefix = string.Empty;
        inlineContent = string.Empty;

        var leadingLength = quoted.Length - quoted.TrimStart().Length;
        var trimmed = quoted[leadingLength..];
        if (!TryGetGitHubAlertKind(trimmed, out _, out _, out _))
        {
            return false;
        }

        var close = trimmed.IndexOf(']', 2);
        if (close < 0)
        {
            return false;
        }

        var contentStart = leadingLength + close + 1;
        while (contentStart < quoted.Length && char.IsWhiteSpace(quoted[contentStart]))
        {
            contentStart++;
        }

        markerPrefix = quoted[..contentStart];
        inlineContent = contentStart >= quoted.Length ? string.Empty : quoted[contentStart..];
        return true;
    }

    private static int GetMarkdownHeadingMarkerLength(string content)
    {
        var index = 0;
        while (index < content.Length && index < 6 && content[index] == '#')
        {
            index++;
        }
        return index > 0 && index < content.Length && content[index] == ' ' ? index : 0;
    }

    private static string MarkMarkdownTableLine(string line, string markerClass, string? comparisonLine)
    {
        var cellsToMark = FindMarkdownTableCellsToMark(line, comparisonLine);
        if (cellsToMark.Count == 0)
        {
            return line;
        }
        var marked = new StringBuilder(line.Length + 64);
        var current = new StringBuilder();
        var cellIndex = line.TrimStart().StartsWith('|') ? -1 : 0;
        var escaped = false;
        foreach (var ch in line)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }
            if (ch == '\\')
            {
                escaped = true;
                current.Append(ch);
                continue;
            }
            if (ch == '|')
            {
                AppendMarkedTableCell(
                    marked,
                    current.ToString(),
                    markerClass,
                    cellsToMark.Contains(cellIndex));
                current.Clear();
                cellIndex++;
                marked.Append('|');
                continue;
            }
            current.Append(ch);
        }
        AppendMarkedTableCell(
            marked,
            current.ToString(),
            markerClass,
            cellsToMark.Contains(cellIndex));
        return marked.ToString();
    }

    private static HashSet<int> FindMarkdownTableCellsToMark(string line, string? comparisonLine)
    {
        var currentCells = SplitMarkdownTableRow(line);
        if (currentCells.Count == 0)
        {
            return [];
        }

        if (comparisonLine is null)
        {
            return Enumerable.Range(0, currentCells.Count).ToHashSet();
        }

        var comparisonCells = SplitMarkdownTableRow(comparisonLine);
        if (comparisonCells.Count == 0)
        {
            return Enumerable.Range(0, currentCells.Count).ToHashSet();
        }

        var cellsToMark = new HashSet<int>();
        for (var index = 0; index < currentCells.Count; index++)
        {
            if (index >= comparisonCells.Count
                || !string.Equals(NormalizeMarkdownTableCell(currentCells[index]), NormalizeMarkdownTableCell(comparisonCells[index]), StringComparison.Ordinal))
            {
                cellsToMark.Add(index);
            }
        }
        return cellsToMark;
    }

    private static string? FindComparableMarkdownTableLine(string line, string[] comparisonLines)
    {
        if (IsMarkdownTableSeparatorRow(line))
        {
            return null;
        }

        var currentCells = SplitMarkdownTableRow(line);
        if (currentCells.Count == 0)
        {
            return null;
        }

        var bestScore = 0;
        string? bestLine = null;
        foreach (var comparisonLine in comparisonLines)
        {
            if (!IsMarkdownTableLine(comparisonLine) || IsMarkdownTableSeparatorRow(comparisonLine))
            {
                continue;
            }

            var comparisonCells = SplitMarkdownTableRow(comparisonLine);
            if (comparisonCells.Count == 0)
            {
                continue;
            }

            var score = CountMatchingLeadingMarkdownTableCells(currentCells, comparisonCells);
            if (score > bestScore)
            {
                bestScore = score;
                bestLine = comparisonLine;
            }
        }

        return bestScore > 0 ? bestLine : null;
    }

    private static int CountMatchingLeadingMarkdownTableCells(List<string> currentCells, List<string> comparisonCells)
    {
        var max = Math.Min(currentCells.Count, comparisonCells.Count);
        var score = 0;
        for (var index = 0; index < max; index++)
        {
            if (!string.Equals(NormalizeMarkdownTableCell(currentCells[index]), NormalizeMarkdownTableCell(comparisonCells[index]), StringComparison.Ordinal))
            {
                break;
            }
            score++;
        }
        return score;
    }

    private static string NormalizeMarkdownTableCell(string cell)
        => Regex.Replace(cell.Trim(), @"\s+", " ");

    private static void AppendMarkedTableCell(StringBuilder marked, string cell, string markerClass, bool shouldMark)
    {
        if (!shouldMark || string.IsNullOrWhiteSpace(cell))
        {
            marked.Append(cell);
            return;
        }

        var leadingLength = cell.Length - cell.TrimStart().Length;
        var trailingLength = cell.Length - cell.TrimEnd().Length;
        var leading = cell[..leadingLength];
        var trailing = cell[(cell.Length - trailingLength)..];
        var value = cell[leadingLength..(cell.Length - trailingLength)];
        marked.Append(leading)
            .Append(WrapRenderedDiff(value, markerClass))
            .Append(trailing);
    }

    private static string WrapRenderedDiff(string value, string markerClass)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        return "<span class=\"" + markerClass + "\">" + value + "</span>";
    }

    private static bool LooksLikeMarkdownTableExcerpt(string excerpt)
        => IsMarkdownTableLine(excerpt) || excerpt.Contains("||", StringComparison.Ordinal);

    private static bool IsMarkdownTableLine(string excerpt)
    {
        var trimmed = excerpt.Trim();
        if (!trimmed.Contains('|', StringComparison.Ordinal))
        {
            return false;
        }
        if (IsMarkdownTableSeparatorRow(trimmed))
        {
            return true;
        }

        var cells = SplitMarkdownTableRow(trimmed);
        if (cells.Count < 2)
        {
            return false;
        }
        if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
        {
            return true;
        }
        return IsLikelyReusableTableFragmentRow(cells);
    }

    private static bool IsLikelyReusableTableFragmentRow(List<string> cells)
        => cells.Count == 3
            && IsCompactTableCell(cells[0])
            && IsCompactTableCell(cells[1])
            && cells[2].Trim().Length >= 8;

    private static bool IsCompactTableCell(string cell)
    {
        var value = cell.Trim().Trim('`');
        return value.Length is > 0 and <= 64 && !value.Any(char.IsWhiteSpace);
    }

    private static List<string> SplitMarkdownTableRow(string excerpt)
    {
        var trimmed = excerpt.Trim();
        if (!trimmed.Contains('|', StringComparison.Ordinal) || IsMarkdownTableSeparatorRow(trimmed))
        {
            return [];
        }

        var cells = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var ch in trimmed)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }
            if (ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (ch == '|')
            {
                cells.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        cells.Add(current.ToString());

        if (trimmed.StartsWith('|') && cells.Count > 0 && string.IsNullOrWhiteSpace(cells[0]))
        {
            cells.RemoveAt(0);
        }
        if (trimmed.EndsWith('|') && cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1]))
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells.Count >= 2 && cells.Any(static cell => !string.IsNullOrWhiteSpace(cell))
            ? cells
            : [];
    }

    private static bool IsMarkdownTableSeparatorRow(string value)
        => TryNormalizeMarkdownTableSeparatorRow(value, out _);

    private static string NormalizeMarkdownTableSeparatorRow(string value)
        => TryNormalizeMarkdownTableSeparatorRow(value, out var normalized) ? normalized : value;

    private static bool TryNormalizeMarkdownTableSeparatorRow(string value, out string normalized)
    {
        normalized = value;
        var trimmed = value.Trim();
        if (!trimmed.Contains('|', StringComparison.Ordinal))
        {
            return false;
        }

        var cells = value.Split('|');
        var firstCell = trimmed.StartsWith('|') ? 1 : 0;
        var lastCell = cells.Length - (trimmed.EndsWith('|') ? 1 : 0);
        if (lastCell - firstCell < 2)
        {
            return false;
        }

        var hasDelimiter = false;
        for (var index = firstCell; index < lastCell; index++)
        {
            var cell = cells[index].Trim();
            if (cell.Length == 0)
            {
                cells[index] = " --- ";
                continue;
            }

            var delimiter = cell.Trim(':');
            if (delimiter.Length == 0 || delimiter.Any(static ch => ch != '-'))
            {
                return false;
            }
            hasDelimiter = true;
        }

        if (!hasDelimiter)
        {
            return false;
        }

        normalized = string.Join('|', cells);
        return true;
    }

    private static string BuildChangeKindSlug(DocsVersionChangeKind kind)
        => kind switch
        {
            DocsVersionChangeKind.Added => "added",
            DocsVersionChangeKind.Removed => "removed",
            DocsVersionChangeKind.Updated => "updated",
            _ => "updated",
        };

    private static string BuildChangeKindLabel(DocsVersionChangeKind kind)
        => kind switch
        {
            DocsVersionChangeKind.Added => "追加",
            DocsVersionChangeKind.Removed => "削除",
            DocsVersionChangeKind.Updated => "更新",
            _ => "更新",
        };

    private sealed record VersionImpactGroup(
        IReadOnlyList<DocsVersion> Versions,
        IReadOnlyList<DocsVersionChangeSnippet> Changes);

    private sealed class VersionImpactGroupBuilder(int firstIndex, IReadOnlyList<DocsVersionChangeSnippet> changes)
    {
        public int FirstIndex { get; } = firstIndex;

        public IReadOnlyList<DocsVersionChangeSnippet> Changes { get; } = changes;

        public List<DocsVersion> Versions { get; } = [];
    }
}
