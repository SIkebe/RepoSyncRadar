using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="MarkdownPreviewRenderer"/>'s github/docs flavored
/// rendering (IMPLEMENTATION_PLAN.md §Step 19.7). The renderer is the only
/// substitute for the Next.js dev server on the "Markdown-first" preview
/// path, so it must safely handle frontmatter and Liquid tags that pervade
/// `content/**/*.md` without crashing or letting raw template syntax leak
/// through as visible noise.
/// </summary>
public sealed class MarkdownPreviewRendererTests
{
    [Fact]
    public void Renders_Title_From_Frontmatter_As_H1()
    {
        var markdown = """
            ---
            title: About GitHub Copilot
            shortTitle: Copilot
            ---

            Body text.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/about-copilot.md",
            markdown,
            "deadbeef",
            "PR HEAD");

        // header h1 must contain the frontmatter title, not the raw repo path.
        Assert.Contains("<h1>About GitHub Copilot</h1>", html, StringComparison.Ordinal);
        Assert.Contains("content/copilot/about-copilot.md", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Intro_As_Lead_Paragraph_When_Provided()
    {
        var markdown = """
            ---
            title: About GitHub Copilot
            intro: 'GitHub Copilot is your AI pair programmer.'
            ---

            ## Overview

            Body text.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/about-copilot.md",
            markdown,
            "deadbeef",
            "PR HEAD");

        Assert.Contains("rsr-intro", html, StringComparison.Ordinal);
        Assert.Contains("GitHub Copilot is your AI pair programmer.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Strips_Frontmatter_Block_From_Body()
    {
        var markdown = """
            ---
            title: Sample
            versions:
              fpt: '*'
            ---

            Real content starts here.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "変更前");

        // The YAML keys must not leak into the rendered body.
        Assert.DoesNotContain("versions:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fpt:", html, StringComparison.Ordinal);
        Assert.Contains("Real content starts here.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Placeholds_Liquid_Block_Tags()
    {
        var markdown = """
            ---
            title: Sample
            ---

            {% ifversion fpt %}
            Plain text in between.
            {% endif %}
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        // Raw `{% ifversion %}` syntax must not leak into a Markdig <p>
        // block. We accept it appearing inside a placeholder <span>, so the
        // assertion targets the markup shape — not the inner text — to stay
        // robust against HTML-encoding changes inside the span.
        Assert.DoesNotContain("<p>{%", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>{% endif", html, StringComparison.Ordinal);
        Assert.Contains("rsr-liquid", html, StringComparison.Ordinal);
        Assert.Contains("Plain text in between.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Placeholds_Liquid_Variable_Tags()
    {
        var markdown = """
            ---
            title: Sample
            ---

            Welcome to {% data variables.product.prodname_copilot %}.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        // The Liquid tag must be wrapped in a placeholder <span>, not
        // rendered as a bare literal inside a Markdig <p>.
        Assert.DoesNotContain("<p>Welcome to {% data variables", html, StringComparison.Ordinal);
        Assert.Contains("rsr-liquid", html, StringComparison.Ordinal);
        // Surrounding prose must remain intact.
        Assert.Contains("Welcome to", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Plain_Markdown_Without_Frontmatter()
    {
        var markdown = "# Hello\n\nWorld";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "CHANGELOG.md",
            markdown,
            "abc1234",
            "PR HEAD");

        // Header h1 falls back to the repo path when no frontmatter title is
        // present. The body H1 from Markdig is preserved (Markdig's
        // AutoIdentifierExtension adds an id=, which we don't pin in tests).
        Assert.Contains("<h1>CHANGELOG.md</h1>", html, StringComparison.Ordinal);
        Assert.Matches("<h1[^>]*>Hello</h1>", html);
        Assert.Contains("<p>World</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Falls_Back_When_Markdown_Is_Null()
    {
        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/about-copilot.md",
            markdown: null,
            sha: "abc1234",
            label: "変更前");

        Assert.Contains("rsr-empty", html, StringComparison.Ordinal);
        Assert.Contains("この時点にはファイルがありません", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("title: 'Quoted'", "Quoted")]
    [InlineData("title: \"Double quoted\"", "Double quoted")]
    [InlineData("title: Bare title", "Bare title")]
    public void Extracts_Title_With_Various_Quote_Styles(string titleLine, string expected)
    {
        var markdown = $"---\n{titleLine}\n---\n\nBody";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains($"<h1>{expected}</h1>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Reacts_To_Data_Color_Mode_Attribute_For_Theme_Toggle()
    {
        // The WPF host toggles theme by setting `<html data-color-mode="dark|light">`
        // (MainWindow.xaml.cs `BuildDocsThemeScript`). The renderer must emit
        // rules selecting on that attribute so the user's explicit choice
        // takes effect on the Markdown-first preview path.
        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            "# Hello",
            "abc1234",
            "PR HEAD");

        Assert.Contains(":root[data-color-mode=\"dark\"]", html, StringComparison.Ordinal);
        Assert.Contains(":root[data-color-mode=\"light\"]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Uses_Css_Variables_So_Theme_Switch_Is_Live()
    {
        // Body / article colours must come from CSS variables (not hard-coded
        // hex literals on the element rules) so attribute-driven theme swaps
        // re-paint without a reload.
        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            "# Hello",
            "abc1234",
            "PR HEAD");

        Assert.Contains("--rsr-bg", html, StringComparison.Ordinal);
        Assert.Contains("--rsr-fg", html, StringComparison.Ordinal);
        Assert.Contains("background:var(--rsr-bg)", html, StringComparison.Ordinal);
        Assert.Contains("color:var(--rsr-fg)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_Light_Choice_Overrides_Os_Dark_Preference()
    {
        // When the user pins light via the toggle, the OS-level dark
        // preference must NOT win. Encoded as `:not([data-color-mode="light"])`
        // on the `@media (prefers-color-scheme: dark)` block.
        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            "# Hello",
            "abc1234",
            "PR HEAD");

        Assert.Contains("@media (prefers-color-scheme: dark)", html, StringComparison.Ordinal);
        Assert.Contains(":not([data-color-mode=\"light\"])", html, StringComparison.Ordinal);
    }
}
