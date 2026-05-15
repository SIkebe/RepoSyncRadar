using System.Net;
using System.Text;
using Markdig;

namespace RepoSyncRadar.Core.Services.Preview;

internal static class MarkdownPreviewRenderer
{
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string RenderDocument(
        string repoPath,
        string? markdown,
        string sha,
        string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var title = WebUtility.HtmlEncode(repoPath.Trim());
        var meta = WebUtility.HtmlEncode($"{label} {ShortSha(sha)}");
        var body = markdown is null
            ? "<p class=\"rsr-empty\">この時点にはファイルがありません。</p>"
            : Markdown.ToHtml(markdown, s_pipeline);

        if (string.IsNullOrWhiteSpace(body))
        {
            body = "<p class=\"rsr-empty\">空の Markdown ファイルです。</p>";
        }

        var html = new StringBuilder(capacity: body.Length + 1800);
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"ja\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(title).AppendLine("</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{margin:0;background:#f6f8fa;color:#24292f;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;line-height:1.55;}");
        html.AppendLine("main{max-width:920px;margin:0 auto;padding:32px 24px 64px;}");
        html.AppendLine("article{background:#fff;border:1px solid #d8dee4;border-radius:6px;padding:28px;}");
        html.AppendLine("header{border-bottom:1px solid #d8dee4;margin-bottom:24px;padding-bottom:16px;}");
        html.AppendLine("h1,h2,h3,h4,h5,h6{line-height:1.25;margin:1.25em 0 .55em;font-weight:650;}");
        html.AppendLine("header h1{font-size:1.55rem;margin:0 0 6px;}");
        html.AppendLine(".rsr-meta{color:#57606a;font-size:.85rem;margin:0;}");
        html.AppendLine("p,ul,ol,pre,blockquote,table{margin:0 0 1rem;}");
        html.AppendLine("a{color:#0969da;}code{background:#afb8c133;border-radius:4px;padding:.12em .28em;font-family:'Cascadia Mono',Consolas,monospace;font-size:.92em;}");
        html.AppendLine("pre{background:#f6f8fa;border-radius:6px;overflow:auto;padding:16px;}pre code{background:transparent;padding:0;}");
        html.AppendLine("blockquote{border-left:4px solid #d0d7de;color:#57606a;padding-left:1rem;}table{border-collapse:collapse;display:block;overflow:auto;}td,th{border:1px solid #d0d7de;padding:6px 13px;}th{background:#f6f8fa;}");
        html.AppendLine(".rsr-empty{color:#57606a;font-style:italic;}");
        html.AppendLine("@media (prefers-color-scheme: dark){body{background:#0d1117;color:#c9d1d9;}article{background:#0d1117;border-color:#30363d;}header{border-color:#30363d}.rsr-meta,.rsr-empty{color:#8b949e;}a{color:#58a6ff;}code{background:#6e768166;}pre{background:#161b22;}blockquote{border-color:#30363d;color:#8b949e;}td,th{border-color:#30363d;}th{background:#161b22;}}");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<main>");
        html.AppendLine("<article data-testid=\"article-body\">");
        html.AppendLine("<header>");
        html.Append("<h1>").Append(title).AppendLine("</h1>");
        html.Append("<p class=\"rsr-meta\">").Append(meta).AppendLine("</p>");
        html.AppendLine("</header>");
        html.AppendLine(body);
        html.AppendLine("</article>");
        html.AppendLine("</main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static string ShortSha(string sha)
        => sha.Length <= 7 ? sha : sha[..7];
}