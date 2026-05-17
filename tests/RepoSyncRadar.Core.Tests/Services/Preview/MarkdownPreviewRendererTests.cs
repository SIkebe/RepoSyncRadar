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
    public void Expands_Liquid_Data_Variables_In_Frontmatter_Title_And_Intro()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["product.prodname_copilot"] = "GitHub Copilot",
            },
            new Dictionary<string, string>(StringComparer.Ordinal));
        var markdown = """
            ---
            title: '{% data variables.product.prodname_copilot %} usage metrics'
            intro: 'Find information about usage metrics for {% data variables.product.prodname_copilot %}.'
            ---
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/reference/copilot-usage-metrics/index.md",
            markdown,
            "abc1234",
            "PR HEAD",
            context);

        Assert.Contains("<h1>GitHub Copilot usage metrics</h1>", html, StringComparison.Ordinal);
        Assert.Contains("Find information about usage metrics for GitHub Copilot.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% data variables.product.prodname_copilot %}", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontmatter_Only_Index_Page_Does_Not_Show_Empty_Markdown_Message()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["product.prodname_copilot"] = "GitHub Copilot",
            },
            new Dictionary<string, string>(StringComparer.Ordinal));
        var markdown = """
            ---
            title: GitHub Copilot usage metrics
            intro: Find information about usage metrics for {% data variables.product.prodname_copilot %}.
            ---
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/reference/copilot-usage-metrics/index.md",
            markdown,
            "abc1234",
            "PR HEAD",
            context);

        Assert.Contains("GitHub Copilot usage metrics", html, StringComparison.Ordinal);
        Assert.Contains("Find information about usage metrics for GitHub Copilot.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("空の Markdown", html, StringComparison.Ordinal);
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
    public void Resolves_Ifversion_To_First_Branch_So_Body_Survives()
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

        // Step 19.8: Liquid evaluator now strips the ifversion gate and emits
        // the first branch as-is. The reviewer therefore sees prose, not a
        // yellow placeholder for the version condition.
        Assert.Contains("Plain text in between.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ifversion", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>{%", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>{% endif", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolves_Ifversion_With_Else_Branch_To_First_Branch_Only()
    {
        var markdown = """
            ---
            title: Sample
            ---

            {% ifversion fpt %}
            primary
            {% elsif ghec %}
            secondary
            {% else %}
            tertiary
            {% endif %}
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        // Only the first branch should survive — preview is rendered for the
        // dotcom audience by default; the other branches are noise for reviewers.
        Assert.Contains("primary", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secondary", html, StringComparison.Ordinal);
        Assert.DoesNotContain("tertiary", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Expands_Data_Variable_From_LiquidContext()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["product.prodname_copilot"] = "GitHub Copilot",
            },
            new Dictionary<string, string>(StringComparer.Ordinal));

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
            "PR HEAD",
            context);

        // The variable must be inlined as literal text, NOT wrapped in a
        // placeholder span: the whole point of Step 19.8 is to make the
        // preview readable, not just visually neutralise the raw syntax.
        Assert.Contains("Welcome to GitHub Copilot.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% data variables", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolved_Variable_Tag_Is_Wrapped_In_Liquid_Placeholder_Span()
    {
        var markdown = """
            ---
            title: Sample
            ---

            Welcome to {% data variables.product.prodname_copilot %}.
            """;

        // Empty context — the variable cannot be resolved.
        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.DoesNotContain("<p>Welcome to {% data variables", html, StringComparison.Ordinal);
        Assert.Contains("rsr-liquid", html, StringComparison.Ordinal);
        Assert.Contains("Welcome to", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Liquid_Placeholder_Span_Is_Not_Escaped_As_Literal_Text()
    {
        // Regression guard for Step 19.8: with DisableHtml() the placeholder
        // <span class="rsr-liquid"> NeutralizeLiquid injected was rendered as
        // literal "&lt;span class=&quot;rsr-liquid&quot;…&gt;" inside <p>,
        // which is what the user reported. The span must reach the browser
        // as real HTML so the highlight is applied.
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

        Assert.DoesNotContain("&lt;span class=&quot;rsr-liquid", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-liquid", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Expands_Reusable_Block_From_LiquidContext()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["product.prodname_copilot"] = "GitHub Copilot",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["copilot.about-copilot"] = "Try {% data variables.product.prodname_copilot %} today.",
            });

        var markdown = """
            ---
            title: Sample
            ---

            {% data reusables.copilot.about-copilot %}
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD",
            context);

        // Reusable block is inlined AND its inner variable is expanded.
        Assert.Contains("Try GitHub Copilot today.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% data reusables", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% data variables", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Expands_Docs_Security_Page_Liquid_Patterns()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["product.github"] = "GitHub",
                ["product.prodname_team"] = "GitHub Team",
                ["product.prodname_ghe_cloud"] = "GitHub Enterprise Cloud",
                ["product.prodname_GHAS"] = "GitHub Advanced Security",
                ["product.prodname_GH_cs_or_sp"] = "{% ifversion ghas-products %}GitHub Secret Protection or GitHub Code Security{% else %}GitHub Advanced Security{% endif %}",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["advanced-security.ghas-products-bullets"] = "* **GitHub Secret Protection**\n* **GitHub Code Security**",
            });

        var markdown = """
            ---
            title: About security
            ---

            {% data variables.product.github %} has features. Additional features are available {% ifversion fpt or ghec %}to organizations on {% data variables.product.prodname_team %} and {% data variables.product.prodname_ghe_cloud %} that{% else %} if you {% endif %} purchase a {% data variables.product.prodname_GHAS %} product:

            {% data reusables.advanced-security.ghas-products-bullets+ghas %}

            For more information on purchasing {% data variables.product.prodname_GH_cs_or_sp %}, see the documentation.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD",
            context,
            DocsVersion.Fpt);

        Assert.Contains("GitHub Secret Protection", html, StringComparison.Ordinal);
        Assert.Contains("GitHub Code Security", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% data", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% ifversion", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% endif", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-liquid", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Preserves_Inline_Html_Like_Picture_Tags()
    {
        // Step 19.8: DisableHtml() was removed so github/docs inline HTML
        // (which appears in many content/**/*.md files for responsive images)
        // reaches the browser. Without this, <picture> showed up as literal text.
        var markdown = """
            ---
            title: Sample
            ---

            <picture>
              <source srcset="dark.png" media="(prefers-color-scheme: dark)">
              <img src="light.png" alt="example">
            </picture>
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("<picture>", html, StringComparison.Ordinal);
        Assert.Contains("<img src=\"light.png\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;picture&gt;", html, StringComparison.Ordinal);
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

    [Fact]
    public void Version_Badges_Are_Buttons_That_Post_Version_Change_Message()
    {
        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            "# Hello",
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt, DocsVersion.Ghec, DocsVersion.Ghes("3.21")],
            selectedVersion: DocsVersion.Ghec);

        Assert.Contains("<button type=\"button\" class=\"rsr-version-badge", html, StringComparison.Ordinal);
        Assert.Contains("data-rsr-version-slug=\"fpt\"", html, StringComparison.Ordinal);
        Assert.Contains("data-rsr-version-slug=\"ghec\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("rsr-preview-version:${slug}", html, StringComparison.Ordinal);
        Assert.Contains("window.chrome?.webview?.postMessage", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_Badge_Current_State_Has_Accessible_Label()
    {
        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            "# Hello",
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt, DocsVersion.Ghec],
            selectedVersion: DocsVersion.Fpt);

        Assert.Contains("aria-label=\"Free, Pro, &amp; Team を表示中\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Enterprise Cloud に切り替え\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_Diff_Summary_Renders_Per_Version_Changes()
    {
        var impacts = new[]
        {
            new DocsVersionImpactDetail(
                DocsVersion.Fpt,
                [new DocsVersionChangeSnippet(DocsVersionChangeKind.Updated, "Free old note", "Free updated note")]),
            new DocsVersionImpactDetail(
                DocsVersion.Ghec,
                [new DocsVersionChangeSnippet(DocsVersionChangeKind.Added, null, "Enterprise Cloud only addition")]),
        };

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            "# Hello",
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt, DocsVersion.Ghec],
            selectedVersion: DocsVersion.Ghec,
            versionImpacts: impacts);

        Assert.Contains("data-testid=\"rsr-version-diff-summary\"", html, StringComparison.Ordinal);
        Assert.Contains("変更パターン", html, StringComparison.Ordinal);
        Assert.Contains("Free old note", html, StringComparison.Ordinal);
        Assert.Contains("Free updated note", html, StringComparison.Ordinal);
        Assert.Contains("Enterprise Cloud only addition", html, StringComparison.Ordinal);
        Assert.Contains("rsr-version-diff-item--current", html, StringComparison.Ordinal);
        Assert.Contains("data-change-kind=\"added\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_Diff_Summary_Groups_Identical_Changes_By_Pattern()
    {
        var sharedChange = new DocsVersionChangeSnippet(DocsVersionChangeKind.Updated, "Shared old note", "Shared updated note");
        var impacts = new[]
        {
            new DocsVersionImpactDetail(DocsVersion.Fpt, [sharedChange]),
            new DocsVersionImpactDetail(DocsVersion.Ghec, [sharedChange]),
            new DocsVersionImpactDetail(
                DocsVersion.Ghes("3.21"),
                [new DocsVersionChangeSnippet(DocsVersionChangeKind.Added, null, "GHES only addition")]),
        };

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            "# Hello",
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt, DocsVersion.Ghec, DocsVersion.Ghes("3.21")],
            selectedVersion: DocsVersion.Ghec,
            versionImpacts: impacts);

        Assert.Contains("2 種類の変更内容があります", html, StringComparison.Ordinal);
        Assert.Contains("変更パターン 1: 2 版で同じ変更", html, StringComparison.Ordinal);
        Assert.Contains("Free, Pro, &amp; Team", html, StringComparison.Ordinal);
        Assert.Contains("Enterprise Cloud", html, StringComparison.Ordinal);
        Assert.Contains("Enterprise Server 3.21 のみ", html, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(html, "Shared old note"));
        Assert.Equal(1, CountOccurrences(html, "Shared updated note"));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}
