using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Renders a single Markdown document to a self-contained HTML page used by
/// the Markdown-first preview path (IMPLEMENTATION_PLAN.md §Step 19.7 / 19.8).
/// This is the substitute for the Next.js dev server: instead of compiling the
/// whole github/docs site we read one file from the worktree and feed it to
/// Markdig directly. Frontmatter is stripped (title / intro promoted to a
/// header), and Liquid tags such as <c>{% data variables.x %}</c> are first
/// evaluated by <see cref="DocsLiquidEvaluator"/> using
/// <see cref="DocsLiquidContext"/> read from the worktree; anything that
/// remains unresolved is wrapped in a grey placeholder span so the raw
/// template syntax never leaks into the rendered body.
/// </summary>
internal static partial class MarkdownPreviewRenderer
{
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        // NOTE: We intentionally do NOT call DisableHtml() here. github/docs
        // markdown sources ship with inline HTML (<picture>, <video>, tables
        // with <thead>, <details>/<summary>, etc.) that are integral to the
        // rendered article; disabling HTML would leak the literal tags as
        // text. The same applies to the <span class="rsr-liquid"> markers
        // NeutralizeLiquid injects for unresolved Liquid tags — with HTML
        // disabled they showed up as raw "<span class=…>" strings in the
        // preview (the visual regression that motivated Step 19.8).
        .Build();

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

    [GeneratedRegex("""<a\b(?<attrs>[^>]*)>(?<body>.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex("""\bhref\s*=\s*(?<quote>["'])(?<href>.*?)\k<quote>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorHrefRegex();

    [GeneratedRegex("""(?<attr>\b(?:src|poster)\s*=\s*)(?<quote>["'])(?<url>[^"']+)\k<quote>""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAssetUrlRegex();

    [GeneratedRegex("""(?<attr>\bsrcset\s*=\s*)(?<quote>["'])(?<value>[^"']+)\k<quote>""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlSrcSetRegex();

    [GeneratedRegex("""<!--.*?-->""", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    private const string CopilotOcticonSvg = """
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
        string? assetBasePath = null)
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
                effectiveVersion);
            var liquidBlocksRendered = RenderOfficialLiquidBlocks(liquidEvaluated);
            var githubAlertsRendered = RenderGitHubAlertBlocks(liquidBlocksRendered);
            var liquidNeutralized = NeutralizeLiquid(githubAlertsRendered);
            body = Markdown.ToHtml(liquidNeutralized, s_pipeline);
            if (!HasVisibleBodyMarkup(body))
            {
                var frontmatterDiffHtml = RenderFrontmatterDiff(frontmatterChanges);
                body = frontmatterDiffHtml + (frontmatterTitle is null && frontmatterIntro is null
                    ? "<p class=\"rsr-empty\">空の Markdown ファイルです。</p>"
                    : "<p class=\"rsr-empty\">このファイルは存在しますが、本文はありません。フロントマターのみ、または自動生成コメントのみの Markdown です。</p>");
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
        html.AppendLine("img,video{max-width:100%;height:auto;}picture{display:block;margin:0 0 1rem;}picture img{margin-bottom:0;}");
        html.AppendLine("blockquote{border-left:4px solid var(--rsr-blockquote-border);color:var(--rsr-muted);padding-left:1rem;}table{border-collapse:collapse;display:block;overflow:auto;}td,th{border:1px solid var(--rsr-border);padding:6px 13px;}th{background:var(--rsr-th-bg);}");
        html.AppendLine(".octicon{display:inline-block;vertical-align:text-bottom;fill:currentColor;overflow:visible;}");
        html.AppendLine(".ghd-alert{border:1px solid var(--rsr-border);border-left-width:4px;border-radius:6px;margin:0 0 1rem;padding:12px 14px;background:var(--rsr-article-bg);}");
        html.AppendLine(".ghd-alert>:last-child,.ghd-tool>:last-child{margin-bottom:0;}");
        html.AppendLine(".ghd-alert-accent{border-left-color:#0969da;}.ghd-alert-success{border-left-color:#1a7f37;}.ghd-alert-attention{border-left-color:#9a6700;}.ghd-alert-danger{border-left-color:#cf222e;}");
        html.AppendLine(".ghd-markdown-alert{border:0;border-left:4px solid var(--rsr-alert-color);border-radius:0;margin:0 0 1rem;padding:8px 0 8px 14px;background:transparent;color:var(--rsr-fg);}");
        html.AppendLine(".ghd-markdown-alert>:last-child{margin-bottom:0;}.ghd-markdown-alert-title{align-items:center;color:var(--rsr-alert-color);display:flex;font-weight:650;gap:6px;margin:0 0 8px;}");
        html.AppendLine(".ghd-markdown-alert-note{--rsr-alert-color:#0969da;}.ghd-markdown-alert-tip{--rsr-alert-color:#1a7f37;}.ghd-markdown-alert-important{--rsr-alert-color:#8250df;}.ghd-markdown-alert-warning{--rsr-alert-color:#9a6700;}.ghd-markdown-alert-caution{--rsr-alert-color:#cf222e;}");
        html.AppendLine(".ghd-tool{border:1px solid var(--rsr-border);border-left:4px solid var(--rsr-link);border-radius:6px;margin:0 0 1rem;padding:12px 14px;background:var(--rsr-pre-bg);}");
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
        html.AppendLine(".rsr-version-diff-item--current{box-shadow:inset 3px 0 0 var(--rsr-link);}");
        html.AppendLine(".rsr-version-diff-title{align-items:center;display:flex;flex-wrap:wrap;gap:6px;margin:0 0 6px;font-size:.84rem;}");
        html.AppendLine(".rsr-version-pattern-versions{display:flex;flex-wrap:wrap;gap:5px;list-style:none;margin:0 0 8px;padding:0;}");
        html.AppendLine(".rsr-version-pattern-badge{display:inline-block;padding:2px 7px;background:var(--rsr-th-bg);border:1px solid var(--rsr-border);border-radius:12px;font:inherit;font-size:.72rem;color:var(--rsr-fg);cursor:pointer;}");
        html.AppendLine(".rsr-version-pattern-badge:hover{border-color:var(--rsr-link);color:var(--rsr-link);}");
        html.AppendLine(".rsr-version-pattern-badge--current{background:var(--rsr-liquid-bg);color:var(--rsr-liquid-fg);border-color:var(--rsr-liquid-border);font-weight:600;}");
        html.AppendLine(".rsr-version-current-chip{border:1px solid var(--rsr-liquid-border);border-radius:999px;color:var(--rsr-liquid-fg);font-size:.7rem;padding:1px 7px;}");
        html.AppendLine(".rsr-version-change{border-left:3px solid var(--rsr-border);display:grid;gap:4px;margin-top:8px;padding-left:8px;}");
        html.AppendLine(".rsr-version-change[data-change-kind='added']{border-left-color:#2da44e;}");
        html.AppendLine(".rsr-version-change[data-change-kind='removed']{border-left-color:#cf222e;}");
        html.AppendLine(".rsr-version-change[data-change-kind='updated']{border-left-color:#bf8700;}");
        html.AppendLine(".rsr-version-change-kind{color:var(--rsr-muted);font-size:.72rem;font-weight:700;}");
        html.AppendLine(".rsr-version-change p{margin:0;font-size:.78rem;}");
        html.AppendLine(".rsr-version-change-label{color:var(--rsr-muted);font-weight:700;margin-right:.35rem;}");
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
        AppendVersionDiffSummary(html, selectedVersion ?? effectiveVersion, versionImpacts);
        AppendSourceDiffSummary(html, sourceDiff);
        html.AppendLine("</header>");
        if (!string.IsNullOrWhiteSpace(introHtml))
        {
            html.Append("<p class=\"rsr-intro\">")
            .Append(introHtml)
                .AppendLine("</p>");
        }
        html.AppendLine(body);
        html.AppendLine("</article>");
        html.AppendLine("</main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
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

    private static string RenderOfficialLiquidBlocks(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var current = content;
        for (var safety = 0; safety < 16; safety++)
        {
            var before = current;
            current = SpotlightBlockRegex().Replace(current, RenderSpotlightBlock);
            current = ToolBlockRegex().Replace(current, RenderToolBlock);
            current = PromptBlockRegex().Replace(current, RenderPromptBlock);
            if (string.Equals(before, current, StringComparison.Ordinal))
            {
                break;
            }
        }
        return current;
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
        if (!TryGetGitHubAlertKind(marker, out var kind, out var label))
        {
            return false;
        }

        var bodyMarkdown = string.Join('\n', blockquoteLines.Skip(1)).Trim('\n');
        var bodyHtml = bodyMarkdown.Length == 0
            ? string.Empty
            : Markdown.ToHtml(RenderGitHubAlertBlocks(bodyMarkdown), s_pipeline).TrimEnd();
        alertHtml = string.Create(
            CultureInfo.InvariantCulture,
            $"\n<div class=\"ghd-markdown-alert ghd-markdown-alert-{kind}\">\n<p class=\"ghd-markdown-alert-title\">{WebUtility.HtmlEncode(label)}</p>\n{bodyHtml}\n</div>\n");
        return true;
    }

    private static bool TryGetGitHubAlertKind(string marker, out string kind, out string label)
    {
        kind = string.Empty;
        label = string.Empty;
        if (marker.Length < 4 || marker[0] != '[' || marker[1] != '!' || marker[^1] != ']')
        {
            return false;
        }

        var alertType = marker[2..^1].Trim();
        (kind, label) = alertType.ToUpperInvariant() switch
        {
            "NOTE" => ("note", "Note"),
            "TIP" => ("tip", "Tip"),
            "IMPORTANT" => ("important", "Important"),
            "WARNING" => ("warning", "Warning"),
            "CAUTION" => ("caution", "Caution"),
            _ => (string.Empty, string.Empty),
        };
        return kind.Length > 0;
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
            $"<code id=\"{promptId}\">{encodedPrompt}</code><a href=\"{encodedHref}\" target=\"_blank\" class=\"tooltipped tooltipped-n ml-1 copilot-prompt-long\" aria-label=\"Run this prompt in Copilot Chat\" aria-describedby=\"{promptId}\" style=\"text-decoration:none;\">{CopilotOcticonSvg}</a><a href=\"{encodedHref}\" target=\"_blank\" class=\"tooltipped tooltipped-n ml-1 copilot-prompt-short\" aria-label=\"Run prompt\" aria-describedby=\"{promptId}\" style=\"text-decoration:none;\">{CopilotOcticonSvg}</a>");
    }

    private static string RenderLiquidBlockBody(string body)
    {
        var nestedBlocksRendered = RenderOfficialLiquidBlocks(body.Trim('\r', '\n'));
        var neutralized = NeutralizeLiquid(nestedBlocksRendered);
        return Markdown.ToHtml(neutralized, s_pipeline).TrimEnd();
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
            if (!string.Equals(WebUtility.HtmlDecode(innerHtml).Trim(), "AUTOTITLE", StringComparison.Ordinal))
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

            return string.Concat("<a", attrs, ">", titleHtml, "</a>");
        });
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

    private static string RewriteAssetReferences(string html, string repoPath, string? assetBasePath)
    {
        if (string.IsNullOrWhiteSpace(assetBasePath) || string.IsNullOrEmpty(html))
        {
            return html;
        }

        var rewritten = HtmlAssetUrlRegex().Replace(html, m =>
        {
            var quote = m.Groups["quote"].Value;
            var url = WebUtility.HtmlDecode(m.Groups["url"].Value);
            var next = RewriteAssetUrl(url, repoPath, assetBasePath);
            return string.Concat(
                m.Groups["attr"].Value,
                quote,
                WebUtility.HtmlEncode(next),
                quote);
        });

        return HtmlSrcSetRegex().Replace(rewritten, m =>
        {
            var quote = m.Groups["quote"].Value;
            var value = WebUtility.HtmlDecode(m.Groups["value"].Value);
            var next = RewriteSrcSet(value, repoPath, assetBasePath);
            return string.Concat(
                m.Groups["attr"].Value,
                quote,
                WebUtility.HtmlEncode(next),
                quote);
        });
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
        IReadOnlyList<DocsVersionImpactDetail>? versionImpacts)
    {
        if (versionImpacts is null || versionImpacts.Count == 0)
        {
            return;
        }

        var groups = BuildVersionImpactGroups(versionImpacts, currentVersion);
        html.Append("<section class=\"rsr-version-diff-summary\" data-testid=\"rsr-version-diff-summary\" aria-label=\"版別差分\">");
        html.Append("<h2>変更パターン</h2>");
        html.Append("<p class=\"rsr-version-diff-overview\">");
        if (groups.Count == 1)
        {
            html.Append(versionImpacts.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" 版で同じ変更内容です。");
        }
        else
        {
            html.Append(groups.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" 種類の変更内容があります。版チップが同じ変更を共有する範囲です。");
        }
        html.Append("</p>");
        html.Append("<ul class=\"rsr-version-diff-list\">");
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            var isCurrent = group.Versions.Contains(currentVersion);
            html.Append("<li class=\"rsr-version-diff-item");
            if (isCurrent)
            {
                html.Append(" rsr-version-diff-item--current");
            }
            html.Append("\">");
            html.Append("<h3 class=\"rsr-version-diff-title\"><span>")
                .Append(WebUtility.HtmlEncode(BuildVersionImpactGroupTitle(group, groupIndex)))
                .Append("</span>");
            if (isCurrent)
            {
                html.Append("<span class=\"rsr-version-current-chip\">表示中</span>");
            }
            html.Append("</h3>");

            AppendVersionPatternBadges(html, group.Versions, currentVersion);

            var visibleChanges = group.Changes.Take(3).ToArray();
            foreach (var change in visibleChanges)
            {
                AppendVersionChange(html, change);
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
        MarkdownSourceDiffSummary? sourceDiff)
    {
        if (sourceDiff is null || !sourceDiff.HasChanges)
        {
            return;
        }

        var totalChanges = sourceDiff.IfversionChanges.Count
            + sourceDiff.RelatedFileChanges.Sum(static file => file.Changes.Count);
        html.Append("<section class=\"rsr-source-diff\" data-testid=\"rsr-source-diff\" aria-label=\"ソース差分\">");
        html.Append("<h2>レンダリングに出ないソース差分</h2>");
        html.Append("<p class=\"rsr-source-diff-overview\">")
            .Append(totalChanges.ToString(CultureInfo.InvariantCulture))
            .Append(" 件の Liquid 条件または関連 data ファイル差分があります。本文が同じに見える場合は、この条件変更を確認してください。</p>");
        html.Append("<ul class=\"rsr-source-diff-list\">");

        foreach (var change in sourceDiff.IfversionChanges.Take(8))
        {
            AppendIfversionSourceChange(html, change);
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
                AppendSourceLineChange(html, lineChange);
            }
            if (fileChange.Changes.Count > 6)
            {
                html.Append("<p class=\"rsr-version-diff-more\">")
                    .Append((fileChange.Changes.Count - 6).ToString(CultureInfo.InvariantCulture))
                    .Append(" 件の追加差分があります</p>");
            }
            html.Append("</li>");
        }

        if (sourceDiff.IfversionChanges.Count > 8 || sourceDiff.RelatedFileChanges.Count > 4)
        {
            html.Append("<li class=\"rsr-version-diff-more\">一部のソース差分のみ表示しています。</li>");
        }
        html.Append("</ul></section>");
    }

    private static void AppendIfversionSourceChange(StringBuilder html, MarkdownIfversionChange change)
    {
        html.Append("<li class=\"rsr-source-change\" data-change-kind=\"")
            .Append(WebUtility.HtmlEncode(BuildChangeKindSlug(change.Kind)))
            .Append("\">");
        html.Append("<span class=\"rsr-source-change-kind\">")
            .Append(WebUtility.HtmlEncode($"ifversion {BuildChangeKindLabel(change.Kind)}"))
            .Append("</span>");
        if (!string.IsNullOrWhiteSpace(change.BeforeExpression))
        {
            AppendSourceLine(html, "変更前", "{% ifversion " + change.BeforeExpression + " %}");
        }
        if (!string.IsNullOrWhiteSpace(change.BeforePreview))
        {
            AppendSourceLine(html, "対象本文", change.BeforePreview);
        }
        if (!string.IsNullOrWhiteSpace(change.AfterExpression))
        {
            AppendSourceLine(html, "PR HEAD", "{% ifversion " + change.AfterExpression + " %}");
        }
        if (!string.IsNullOrWhiteSpace(change.AfterPreview)
            && !string.Equals(change.BeforePreview, change.AfterPreview, StringComparison.Ordinal))
        {
            AppendSourceLine(html, "対象本文", change.AfterPreview);
        }
        html.Append("</li>");
    }

    private static void AppendSourceLineChange(StringBuilder html, MarkdownSourceLineChange change)
    {
        if (!string.IsNullOrWhiteSpace(change.BeforeLine))
        {
            AppendSourceLine(html, "変更前", change.BeforeLine);
        }
        if (!string.IsNullOrWhiteSpace(change.AfterLine))
        {
            AppendSourceLine(html, "PR HEAD", change.AfterLine);
        }
    }

    private static void AppendSourceLine(StringBuilder html, string label, string line)
    {
        html.Append("<p class=\"rsr-source-line\"><span class=\"rsr-source-line-label\">")
            .Append(WebUtility.HtmlEncode(label))
            .Append("</span><code>")
            .Append(WebUtility.HtmlEncode(line))
            .Append("</code></p>");
    }

    private static string RenderFrontmatterDiff(IReadOnlyList<MarkdownFrontmatterChange>? changes)
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
            .Append(" 件のメタデータ差分があります。本文がないため、レビュー対象は主にこの YAML です。</p>");
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
        IReadOnlyList<DocsVersionImpactDetail> versionImpacts,
        DocsVersion currentVersion)
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

        builders.Sort((left, right) =>
        {
            var leftIsCurrent = left.Versions.Contains(currentVersion);
            var rightIsCurrent = right.Versions.Contains(currentVersion);
            if (leftIsCurrent != rightIsCurrent)
            {
                return leftIsCurrent ? -1 : 1;
            }
            return left.FirstIndex.CompareTo(right.FirstIndex);
        });

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
        IReadOnlyList<DocsVersion> versions,
        DocsVersion currentVersion)
    {
        html.Append("<ul class=\"rsr-version-pattern-versions\" aria-label=\"この変更が出る版\">");
        foreach (var version in versions)
        {
            var isCurrent = version == currentVersion;
            html.Append("<li><button type=\"button\" class=\"rsr-version-pattern-badge");
            if (isCurrent)
            {
                html.Append(" rsr-version-pattern-badge--current");
            }
            html.Append("\" data-rsr-version-slug=\"")
                .Append(WebUtility.HtmlEncode(version.Slug))
                .Append("\" data-version-slug=\"")
                .Append(WebUtility.HtmlEncode(version.Slug))
                .Append('"');
            if (isCurrent)
            {
                html.Append(" aria-current=\"true\" aria-label=\"")
                    .Append(WebUtility.HtmlEncode(version.DisplayLabel + " を表示中"))
                    .Append('"');
            }
            else
            {
                html.Append(" aria-label=\"")
                    .Append(WebUtility.HtmlEncode(version.DisplayLabel + " に切り替え"))
                    .Append('"');
            }
            html.Append('>')
                .Append(WebUtility.HtmlEncode(version.DisplayLabel))
                .Append("</button></li>");
        }
        html.Append("</ul>");
    }

    private static void AppendVersionChange(StringBuilder html, DocsVersionChangeSnippet change)
    {
        html.Append("<div class=\"rsr-version-change\" data-change-kind=\"")
            .Append(WebUtility.HtmlEncode(BuildChangeKindSlug(change.Kind)))
            .Append("\">");
        html.Append("<span class=\"rsr-version-change-kind\">")
            .Append(WebUtility.HtmlEncode(BuildChangeKindLabel(change.Kind)))
            .Append("</span>");
        if (!string.IsNullOrWhiteSpace(change.BeforeExcerpt))
        {
            html.Append("<p><span class=\"rsr-version-change-label\">変更前</span>")
                .Append(WebUtility.HtmlEncode(change.BeforeExcerpt))
                .Append("</p>");
        }
        if (!string.IsNullOrWhiteSpace(change.AfterExcerpt))
        {
            html.Append("<p><span class=\"rsr-version-change-label\">PR HEAD</span>")
                .Append(WebUtility.HtmlEncode(change.AfterExcerpt))
                .Append("</p>");
        }
        html.Append("</div>");
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