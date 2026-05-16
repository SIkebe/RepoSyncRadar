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

    public static string RenderDocument(
        string repoPath,
        string? markdown,
        string sha,
        string label,
        DocsLiquidContext? liquidContext = null,
        DocsVersion? version = null,
        IReadOnlyList<DocsVersion>? affectedVersions = null,
        DocsVersion? selectedVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var effectiveVersion = version ?? DocsVersionCatalog.Default;
        var repoPathDisplay = WebUtility.HtmlEncode(repoPath.Trim());
        var meta = WebUtility.HtmlEncode($"{label} {ShortSha(sha)}");
        string title;
        string? intro;
        string body;

        if (markdown is null)
        {
            title = repoPathDisplay;
            intro = null;
            body = "<p class=\"rsr-empty\">この時点にはファイルがありません。</p>";
        }
        else
        {
            var (frontmatter, content) = SplitFrontmatter(markdown);
            title = WebUtility.HtmlEncode(
                ExtractFrontmatterScalar(frontmatter, "title")
                ?? repoPath.Trim());
            intro = ExtractFrontmatterScalar(frontmatter, "intro");
            // First expand Liquid tags whose definitions we found in the
            // worktree (variables / reusables / ifversion per `effectiveVersion`);
            // any tag left behind is then wrapped in <span class="rsr-liquid"> by
            // NeutralizeLiquid so the reviewer still sees its original syntax.
            var liquidEvaluated = DocsLiquidEvaluator.Evaluate(
                content,
                liquidContext ?? DocsLiquidContext.Empty,
                effectiveVersion);
            var liquidNeutralized = NeutralizeLiquid(liquidEvaluated);
            body = Markdown.ToHtml(liquidNeutralized, s_pipeline);
            if (string.IsNullOrWhiteSpace(body))
            {
                body = "<p class=\"rsr-empty\">空の Markdown ファイルです。</p>";
            }
        }

        var html = new StringBuilder(capacity: body.Length + 2200);
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"ja\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(title).AppendLine("</title>");
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
        html.AppendLine("blockquote{border-left:4px solid var(--rsr-blockquote-border);color:var(--rsr-muted);padding-left:1rem;}table{border-collapse:collapse;display:block;overflow:auto;}td,th{border:1px solid var(--rsr-border);padding:6px 13px;}th{background:var(--rsr-th-bg);}");
        html.AppendLine(".rsr-liquid{display:inline-block;background:var(--rsr-liquid-bg);color:var(--rsr-liquid-fg);border:1px solid var(--rsr-liquid-border);border-radius:3px;padding:0 .35em;margin:0 .15em;font-size:.82em;font-family:'Cascadia Mono',Consolas,monospace;}");
        html.AppendLine(".rsr-empty{color:var(--rsr-muted);font-style:italic;}");
        html.AppendLine(".rsr-version-bar{margin:10px 0 0;display:flex;flex-wrap:wrap;gap:8px;align-items:center;font-size:.82rem;}");
        html.AppendLine(".rsr-version-current{color:var(--rsr-muted);}");
        html.AppendLine(".rsr-version-impact-label{color:var(--rsr-muted);}");
        html.AppendLine(".rsr-version-badges{display:inline-flex;flex-wrap:wrap;gap:6px;padding:0;margin:0;list-style:none;}");
        html.AppendLine(".rsr-version-badge{display:inline-block;padding:2px 8px;background:var(--rsr-th-bg);border:1px solid var(--rsr-border);border-radius:12px;font-size:.76rem;color:var(--rsr-fg);}");
        html.AppendLine(".rsr-version-badge--current{background:var(--rsr-liquid-bg);color:var(--rsr-liquid-fg);border-color:var(--rsr-liquid-border);font-weight:600;}");
        html.AppendLine(".rsr-version-empty{color:var(--rsr-muted);font-style:italic;}");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<main>");
        html.AppendLine("<article data-testid=\"article-body\">");
        html.AppendLine("<header>");
        html.Append("<h1>").Append(title).AppendLine("</h1>");
        html.Append("<p class=\"rsr-meta\">").Append(meta).AppendLine("</p>");
        if (!string.Equals(title, repoPathDisplay, StringComparison.Ordinal))
        {
            // Surface the source repo path when the frontmatter title differs
            // from it, so reviewers can still match the rendered page back to
            // the file they clicked on.
            html.Append("<p class=\"rsr-path\">").Append(repoPathDisplay).AppendLine("</p>");
        }
        AppendVersionBadgeMarkup(html, selectedVersion ?? effectiveVersion, affectedVersions);
        html.AppendLine("</header>");
        if (!string.IsNullOrWhiteSpace(intro))
        {
            html.Append("<p class=\"rsr-intro\">")
                .Append(WebUtility.HtmlEncode(intro))
                .AppendLine("</p>");
        }
        html.AppendLine(body);
        html.AppendLine("</article>");        html.AppendLine("</main>");
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
            html.Append("<span class=\"rsr-version-empty\">この PR ではどの版にも差分はありません。</span>");
        }
        else
        {
            html.Append("<span class=\"rsr-version-impact-label\">この PR で差分のある版:</span>");
            html.Append("<ul class=\"rsr-version-badges\">");
            foreach (var version in affectedVersions)
            {
                var isCurrent = version == currentVersion;
                html.Append("<li class=\"rsr-version-badge");
                if (isCurrent)
                {
                    html.Append(" rsr-version-badge--current");
                }
                html.Append("\" data-version-slug=\"")
                    .Append(WebUtility.HtmlEncode(version.Slug))
                    .Append("\">")
                    .Append(WebUtility.HtmlEncode(version.DisplayLabel))
                    .Append("</li>");
            }
            html.Append("</ul>");
        }
        html.AppendLine("</div>");
    }
}