using System.Text.RegularExpressions;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="MarkdownPreviewRenderer"/>'s github/docs flavored
/// rendering (IMPLEMENTATION_PLAN.md §Step 19.7). The renderer is the only
/// Markdown-first preview renderer, so it must safely handle frontmatter and Liquid tags that pervade
/// `content/**/*.md` without crashing or letting raw template syntax leak
/// through as visible noise.
/// </summary>
public sealed partial class MarkdownPreviewRendererTests
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
    public void Expands_DataSequence_ForLoops_In_Body()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal)
            {
                ["tables.copilot.models-and-pricing"] =
                [
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["model"] = "GPT-5",
                        ["provider"] = "openai",
                        ["input"] = "$1.00",
                        ["agent"] = "{% octicon &quot;check&quot; aria-label=&quot;Included&quot; %}",
                        ["ask"] = "{% octicon &quot;x&quot; aria-label=&quot;Not supported&quot; %}",
                        ["edit"] = "{% octicon &quot;dash&quot; aria-label=&quot;Not applicable&quot; %}",
                        ["notes"] = "{% octicon &quot;pencil&quot; aria-label=&quot;Edit&quot; %}",
                    },
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["model"] = "Claude Sonnet",
                        ["provider"] = "anthropic",
                        ["input"] = "$3.00",
                    },
                ],
            });
        var markdown = """
            ---
            title: Pricing tables
            ---

            | Model | Input | Agent | Ask | Edit | Notes |
            | --- | ---: | --- | --- | --- | --- |
            | {% for entry in tables.copilot.models-and-pricing %}{% if entry.provider == "openai" %} |
            | {{ entry.model }} | {{ entry.input }} | {{ entry.agent }} | {{ entry.ask }} | {{ entry.edit }} | {{ entry.notes }} |
            | {% endif %}{% endfor %} |
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/reference/copilot-billing/models-and-pricing.md",
            markdown,
            "abc1234",
            "PR HEAD",
            context);

        Assert.Contains("GPT-5", html, StringComparison.Ordinal);
        Assert.Contains("$1.00", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-check\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-x\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-dash\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-pencil rsr-octicon-fallback\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Included\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Claude Sonnet", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% for", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% octicon", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;quot;check", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{ entry.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Rowheaders_Table_With_Empty_Delimiter_Cell()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["product.prodname_vscode"] = "Visual Studio Code",
                ["product.prodname_vs"] = "Visual Studio",
                ["copilot.copilot_gemini_35_flash"] = "Gemini 3.5 Flash",
                ["copilot.copilot_gemini_36_flash"] = "Gemini 3.6 Flash",
            },
            new Dictionary<string, string>(StringComparer.Ordinal));
        const string beforeMarkdown = """
            {% rowheaders %}

            | Model | {% data variables.product.prodname_vscode %} | {% data variables.product.prodname_vs %} | JetBrains IDEs | Xcode | Eclipse |
            | --- | --- | --- | --- | --- | --- |
            | {% data variables.copilot.copilot_gemini_35_flash %} | `v1.115.0` | `17.14.22` or `18.1.0` | `1.5.62` | `0.46.0` | `0.14.0` |

            {% endrowheaders %}
            """;
        const string afterMarkdown = """
            {% rowheaders %}

            | Model | {% data variables.product.prodname_vscode %} | {% data variables.product.prodname_vs %} | JetBrains IDEs | Xcode | Eclipse |
            | --- |  | --- | --- | --- | --- |
            | {% data variables.copilot.copilot_gemini_35_flash %} | `v1.115.0` | `17.14.22` or `18.1.0` | `1.5.62` | `0.46.0` | `0.14.0` |
            | {% data variables.copilot.copilot_gemini_36_flash %} | TBD | `17.14.22` or `18.1.0` | TBD | TBD | TBD |

            {% endrowheaders %}
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/reference/ai-models/supported-models.md",
            afterMarkdown,
            "0ffe479",
            "PR HEAD",
            context,
            diffAgainstMarkdown: beforeMarkdown,
            diffAgainstLiquidContext: context,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<th>Visual Studio Code</th>", html, StringComparison.Ordinal);
        Assert.Contains("Gemini 3.6 Flash", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>| Model | Visual Studio Code", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_Not_Normalize_Delimiter_Shaped_Table_Data_Row()
    {
        const string markdown = """
            | A | B |
            | --- | --- |
            | value | value |
            | --- |  |
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("<td>---</td>\n<td></td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_Not_Normalize_Table_Delimiters_Inside_Code_Blocks()
    {
        const string markdown = """
            ```markdown
            | --- |  | --- |
            ```

                | --- |  | --- |
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("| --- |  | --- |", html, StringComparison.Ordinal);
        Assert.DoesNotContain("| --- | --- | --- |", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_Not_Treat_Incompatible_Fences_As_Code_Block_Closers()
    {
        const string markdown = """
            ````markdown
            ```markdown
            | --- |  | --- |
            ```
            ````

            ```markdown
            ~~~
            | :--- |  | ---: |
            ~~~
            ```

            ```markdown
            ```not-a-closer
            | :---: |  | --- |
            ```
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("| --- |  | --- |", html, StringComparison.Ordinal);
        Assert.Contains("| :--- |  | ---: |", html, StringComparison.Ordinal);
        Assert.Contains("| :---: |  | --- |", html, StringComparison.Ordinal);
        Assert.DoesNotContain("| --- | --- | --- |", html, StringComparison.Ordinal);
        Assert.DoesNotContain("| :--- | --- | ---: |", html, StringComparison.Ordinal);
        Assert.DoesNotContain("| :---: | --- | --- |", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_Not_Normalize_Table_Delimiters_Inside_List_Code_Fences()
    {
        const string markdown = """
            - ```markdown
              | Model | IDE |
              | --- |  |
              ```

            | Model | IDE |
            | --- |  |
            | Gemini 3.6 Flash | TBD |
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("| --- |  |", html, StringComparison.Ordinal);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Gemini 3.6 Flash</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_Not_Normalize_Standalone_Or_Html_Table_Delimiters()
    {
        const string markdown = """
            | --- |  | --- |

            <pre>
            | --- |  | --- |
            </pre>
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("| --- |  | --- |", html, StringComparison.Ordinal);
        Assert.DoesNotContain("| --- | --- | --- |", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontmatter_Only_Index_Page_Shows_Metadata_Only_Message()
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
        Assert.Contains("このファイルは存在しますが、本文はありません", html, StringComparison.Ordinal);
        Assert.Contains("フロントマターのみ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("空の Markdown", html, StringComparison.Ordinal);
        Assert.DoesNotContain("この時点にはファイルがありません", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontmatter_With_Only_Autogenerated_Comment_Shows_Metadata_Only_Message()
    {
        var markdown = """
            ---
            title: REST API endpoints for Copilot cloud agent repository management
            shortTitle: Cloud agent repository management
            ---

            <!-- Content after this section is automatically generated -->
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/rest/copilot/copilot-cloud-agent-management.md",
            markdown,
            "4840cbd",
            "PR HEAD");

        Assert.Contains("REST API endpoints for Copilot cloud agent repository management", html, StringComparison.Ordinal);
        Assert.Contains("このファイルは存在しますが、本文はありません", html, StringComparison.Ordinal);
        Assert.Contains("自動生成コメントのみ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Content after this section is automatically generated", html, StringComparison.Ordinal);
        Assert.DoesNotContain("この時点にはファイルがありません", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontmatter_Only_Document_Can_Show_Readable_Frontmatter_Diff()
    {
        var before = """
            ---
            title: REST API endpoints for Copilot
            shortTitle: Copilot
            intro: Use the REST API to monitor and manage GitHub Copilot.
            children:
              - /copilot-coding-agent-management
            ---

            <!-- Content after this section is automatically generated -->
            """;
        var after = """
            ---
            title: REST API endpoints for Copilot cloud agent repository management
            shortTitle: Cloud agent repository management
            intro: Use the REST API to manage repository-level settings for Copilot coding agent.
            children:
              - /copilot-cloud-agent-management
            ---

            <!-- Content after this section is automatically generated -->
            """;
        var changes = MarkdownFrontmatterDiffAnalyzer.Analyze(before, after);

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/rest/copilot/copilot-cloud-agent-management.md",
            after,
            "4840cbd",
            "PR HEAD",
            frontmatterChanges: changes);

        Assert.Contains("data-testid=\"rsr-frontmatter-diff\"", html, StringComparison.Ordinal);
        Assert.Contains("フロントマターの変更", html, StringComparison.Ordinal);
        Assert.Contains("本文がないため、レビュー対象は主にこの YAML", html, StringComparison.Ordinal);
        Assert.Contains("title: REST API endpoints for Copilot", html, StringComparison.Ordinal);
        Assert.Contains("title: REST API endpoints for Copilot cloud agent repository management", html, StringComparison.Ordinal);
        Assert.Contains("- /copilot-coding-agent-management", html, StringComparison.Ordinal);
        Assert.Contains("- /copilot-cloud-agent-management", html, StringComparison.Ordinal);
        Assert.Contains("このファイルは存在しますが、本文はありません", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_With_Body_Can_Show_Readable_Frontmatter_Diff()
    {
        var before = """
            ---
            title: Preparing your organization for usage-based billing
            permissions: Enterprise owners, organization owners, and billing managers
            ---

            Same rendered body.
            """;
        var after = """
            ---
            title: Preparing your organization for usage-based billing
            permissions: Enterprise owners and billing managers can download the usage report for enterprises.
            Organization owners can download the usage report for standalone organizations.
            ---

            Same rendered body.
            """;
        var changes = MarkdownFrontmatterDiffAnalyzer.Analyze(before, after);

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/manage-and-track-spending/prepare-for-usage-based-billing.md",
            after,
            "ef79d33",
            "PR HEAD",
            frontmatterChanges: changes);

        Assert.Contains("data-testid=\"rsr-frontmatter-diff\"", html, StringComparison.Ordinal);
        Assert.Contains("permissions: Enterprise owners, organization owners, and billing managers", html, StringComparison.Ordinal);
        Assert.Contains("permissions: Enterprise owners and billing managers can download the usage report for enterprises.", html, StringComparison.Ordinal);
        Assert.Contains("Organization owners can download the usage report for standalone organizations.", html, StringComparison.Ordinal);
        Assert.Contains("Same rendered body.", html, StringComparison.Ordinal);
        Assert.Contains("本文の差分とあわせて確認してください", html, StringComparison.Ordinal);
        Assert.DoesNotContain("本文がないため、レビュー対象は主にこの YAML", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontmatter_Diff_Analyzer_Reports_Added_File_Frontmatter()
    {
        var after = """
            ---
            title: New API page
            autogenerated: rest
            ---
            """;

        var changes = MarkdownFrontmatterDiffAnalyzer.Analyze(null, after);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal(DocsVersionChangeKind.Added, change.Kind));
        Assert.Contains(changes, change => change.AfterLine == "title: New API page");
        Assert.Contains(changes, change => change.AfterLine == "autogenerated: rest");
    }

    [Fact]
    public void Frontmatter_Diff_Analyzer_Handles_Large_Rewrites_In_Linear_Memory()
    {
        var before = $"---\n{string.Join('\n', Enumerable.Range(0, 10_000).Select(static index => $"before-{index}: value"))}\n---";
        var after = $"---\n{string.Join('\n', Enumerable.Range(0, 10_000).Select(static index => $"after-{index}: value"))}\n---";

        var changes = MarkdownFrontmatterDiffAnalyzer.Analyze(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(DocsVersionChangeKind.Updated, change.Kind);
        Assert.Equal("before-0: value", change.BeforeLine);
        Assert.Equal("after-0: value", change.AfterLine);
    }

    [Fact]
    public void Frontmatter_Diff_Analyzer_Reports_Only_First_Mismatch_For_Large_Moves()
    {
        var lines = Enumerable.Range(0, 1_000).Select(static index => $"line-{index}: value").ToArray();
        var before = $"---\n{string.Join('\n', lines)}\n---";
        var after = $"---\n{string.Join('\n', lines.Skip(1).Append(lines[0]))}\n---";

        var changes = MarkdownFrontmatterDiffAnalyzer.Analyze(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(DocsVersionChangeKind.Updated, change.Kind);
        Assert.Equal("line-0: value", change.BeforeLine);
        Assert.Equal("line-1: value", change.AfterLine);
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
    public void Rewrites_Autotitle_Link_Text_From_Page_Title_Index()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/code-security/how-tos/secure-at-scale/configure-organization-security"] = "Applying security configurations in your organization",
            });

        var markdown = "See [AUTOTITLE](/code-security/how-tos/secure-at-scale/configure-organization-security#creating-a-custom-security-configuration).";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/code-security/how-tos/secure-at-scale/apply-security-configuration.md",
            markdown,
            "abc1234",
            "PR HEAD",
            context);

        Assert.Contains(">Applying security configurations in your organization</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/code-security/how-tos/secure-at-scale/configure-organization-security#creating-a-custom-security-configuration\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">AUTOTITLE</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrites_Relative_Autotitle_Link_Text_And_Evaluates_Target_Title_Liquid()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["product.prodname_copilot"] = "GitHub Copilot",
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["content/copilot/concepts/about-copilot.md"] = "About {% data variables.product.prodname_copilot %}",
            });

        var markdown = "For more information, see [AUTOTITLE](../concepts/about-copilot.md?tool=vscode).";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/use-copilot.md",
            markdown,
            "abc1234",
            "PR HEAD",
            context);

        Assert.Contains(">About GitHub Copilot</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"../concepts/about-copilot.md?tool=vscode\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">AUTOTITLE</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrites_Autotitle_Markdown_Link_And_Preserves_Title_Attribute()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/copilot/concepts/billing/budgets-for-usage-based-billing"] = "Budgets for usage-based billing",
            });

        var markdown = "See [AUTOTITLE](/copilot/concepts/billing/budgets-for-usage-based-billing \"Budget controls\").";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/billing/concepts/budgets-and-alerts.md",
            markdown,
            "abc1234",
            "PR HEAD",
            context);

        Assert.Contains("<a href=\"/copilot/concepts/billing/budgets-for-usage-based-billing\" title=\"Budget controls\">Budgets for usage-based billing</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOTITLE", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrites_Autotitle_Link_Text_When_Diff_Span_Wraps_Body()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/using-the-audit-log-api-for-your-enterprise"] = "Using the audit log API for your enterprise",
            });

        var markdown = "See <a href=\"/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/using-the-audit-log-api-for-your-enterprise\"><span class=\"rsr-rendered-diff-added\">AUTOTITLE</span></a>.";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/exporting-audit-log-activity-for-your-enterprise.md",
            markdown,
            "abc1234",
            "PR HEAD",
            context);

        Assert.Contains("<span class=\"rsr-rendered-diff-added\">Using the audit log API for your enterprise</span></a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">AUTOTITLE</span></a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedDiff_Rewrites_Autotitle_When_Changed_List_Item_Adds_Link()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["copilot/concepts/billing/budgets-for-usage-based-billing"] = "Budgets for usage-based billing",
            });
        const string beforeMarkdown = """
            ## Types and scopes

            * **Scope**: Defines whether the budget applies to the whole account, or to a subset of repositories, organizations, or cost centers (enterprise only).
            """;
        const string afterMarkdown = """
            ## Types and scopes

            * **Scope**: Defines whether the budget applies to the whole account, or to a subset of repositories, organizations, cost centers (enterprise only), or users. User-scoped budgets are currently only supported for Copilot AI credits. See [AUTOTITLE](/copilot/concepts/billing/budgets-for-usage-based-billing).
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/billing/concepts/budgets-and-alerts.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            context,
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("href=\"/copilot/concepts/billing/budgets-for-usage-based-billing\"", html, StringComparison.Ordinal);
        Assert.Contains("Budgets for usage-based billing</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOTITLE", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrites_Autotitle_Markdown_Links_Only_Outside_Code()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["copilot/concepts/billing/budgets-for-usage-based-billing"] = "Budgets for usage-based billing",
            });
        const string beforeMarkdown = """
            ## Types and scopes

            Existing guidance.
            """;
        const string afterMarkdown = """
            ## Types and scopes

            See [AUTOTITLE](/copilot/concepts/billing/budgets-for-usage-based-billing).

            Use `[AUTOTITLE](/copilot/concepts/billing/budgets-for-usage-based-billing)` in docs examples.

            ```markdown
            See [AUTOTITLE](/copilot/concepts/billing/budgets-for-usage-based-billing).
            ```
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/billing/concepts/budgets-and-alerts.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            context,
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<a href=\"/copilot/concepts/billing/budgets-for-usage-based-billing\">Budgets for usage-based billing</a>", html, StringComparison.Ordinal);
        Assert.Contains("<code>[AUTOTITLE](/copilot/concepts/billing/budgets-for-usage-based-billing)</code>", html, StringComparison.Ordinal);
        Assert.Contains("See [AUTOTITLE](/copilot/concepts/billing/budgets-for-usage-based-billing).", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<code>&lt;a href=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedDiff_Does_Not_Expand_Autotitle_Link_Range_From_Earlier_Bracketed_Text()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/new-target"] = "New target",
            });
        const string beforeMarkdown = """
            Use [preview]. See [AUTOTITLE](/old-target).
            """;
        const string afterMarkdown = """
            Use [preview]. See [AUTOTITLE](/new-target).
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            context,
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("Use [preview]. <span class=\"rsr-rendered-diff-added\">See <a href=\"/new-target\">New target</a>.</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\">Use [preview]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedDiff_Marks_Autotitle_Changes_From_Comparison_Liquid_Context()
    {
        const string markdown = """
            > [!NOTE]
            > If you want your developers to have access to local MCP servers, include those servers in your registry with the correct server ID. For more information, see [AUTOTITLE](/copilot/reference/mcp-allowlist-enforcement).
            """;
        var beforeContext = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/copilot/reference/mcp-allowlist-enforcement"] = "MCP allowlist enforcement",
            });
        var afterContext = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/copilot/reference/mcp-allowlist-enforcement"] = "MCP private registry enforcement",
            });

        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/administer-copilot/manage-mcp-usage/configure-mcp-registry.md",
            markdown,
            "501a9d1",
            "変更前",
            beforeContext,
            diffAgainstMarkdown: markdown,
            diffAgainstLiquidContext: afterContext,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);
        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/administer-copilot/manage-mcp-usage/configure-mcp-registry.md",
            markdown,
            "c19e3ad",
            "PR HEAD",
            afterContext,
            diffAgainstMarkdown: markdown,
            diffAgainstLiquidContext: beforeContext,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains(
            "MCP <span class=\"rsr-rendered-diff-removed\">allowlist</span> enforcement</a>",
            beforeHtml,
            StringComparison.Ordinal);
        Assert.Contains(
            "MCP <span class=\"rsr-rendered-diff-added\">private registry</span> enforcement</a>",
            afterHtml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedDiff_Marks_Relative_Autotitle_Changes_Caused_By_Rename()
    {
        const string markdown = "See [AUTOTITLE](../target.md).";
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["content/target.md"] = "Root target",
                ["content/area/target.md"] = "Area target",
            });

        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/area/source.md",
            markdown,
            "501a9d1",
            "変更前",
            context,
            diffAgainstMarkdown: markdown,
            diffAgainstLiquidContext: context,
            diffAgainstRepoPath: "content/area/sub/source.md",
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);
        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/area/sub/source.md",
            markdown,
            "c19e3ad",
            "PR HEAD",
            context,
            diffAgainstMarkdown: markdown,
            diffAgainstLiquidContext: context,
            diffAgainstRepoPath: "content/area/source.md",
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("rsr-rendered-diff-removed", beforeHtml, StringComparison.Ordinal);
        Assert.Contains("Root", beforeHtml, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-added", afterHtml, StringComparison.Ordinal);
        Assert.Contains("Area", afterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedDiff_Does_Not_Insert_Span_Into_Autotitle_Href_With_Comparison_Contexts()
    {
        const string beforeMarkdown = "See [AUTOTITLE](/old-target).";
        const string afterMarkdown = "See [AUTOTITLE](/new-target).";
        var beforeContext = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/old-target"] = "Old target",
            });
        var afterContext = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/new-target"] = "New target",
            });

        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            beforeMarkdown,
            "501a9d1",
            "変更前",
            beforeContext,
            diffAgainstMarkdown: afterMarkdown,
            diffAgainstLiquidContext: afterContext,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);
        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "c19e3ad",
            "PR HEAD",
            afterContext,
            diffAgainstMarkdown: beforeMarkdown,
            diffAgainstLiquidContext: beforeContext,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains(
            "<span class=\"rsr-rendered-diff-removed\"><a href=\"/old-target\">Old target</a></span>",
            beforeHtml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<span class=\"rsr-rendered-diff-added\"><a href=\"/new-target\">New target</a></span>",
            afterHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/old<span", beforeHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"/new<span", afterHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%3Cspan", beforeHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%3Cspan", afterHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderedDiff_Does_Not_Insert_Span_Into_Autotitle_Link_Destination()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/streaming-the-audit-log-for-your-enterprise"] = "Streaming the audit log for your enterprise",
            });
        const string beforeMarkdown = """
            ## Export limits

            There is a hard limit when exporting audit logs.
            """;
        const string afterMarkdown = """
            ## Export limits

            If you intend to review a large dataset of audit logs, we recommend streaming your logs to an external data management system. For more information, see [AUTOTITLE](/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/streaming-the-audit-log-for-your-enterprise).
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/exporting-audit-log-activity-for-your-enterprise.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            context,
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("href=\"/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/streaming-the-audit-log-for-your-enterprise\"", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\">If you intend", html, StringComparison.Ordinal);
        Assert.Contains("Streaming the audit log for your enterprise</a>.</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("%3C/span%3E", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(">AUTOTITLE</span></a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_Unresolved_Autotitle_Link_Text_Untouched()
    {
        var markdown = "See [AUTOTITLE](/missing/page).";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains(">AUTOTITLE</a>", html, StringComparison.Ordinal);
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
    public void Liquid_Syntax_Inside_Inline_Code_Remains_Code_Text()
    {
        var markdown = """
            ---
            title: Sample
            ---

            An alternative is to use the inverse of the `cancelled()` function, `${{ !cancelled() }}`.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/actions/how-tos/troubleshoot-workflows.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("<code>${{ !cancelled() }}</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Liquid 変数", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;span class=&quot;rsr-liquid", html, StringComparison.Ordinal);
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
    public void Treats_Reusable_Added_In_Comparison_As_Added_Rendered_Content()
    {
        const string reusableKey = "enterprise.repo-policy-rules-manage-bypass-request";
        const string markdown = """
            ---
            title: Governing how people use repositories
            ---

            ## Managing bypass requests

            {% data reusables.enterprise.repo-policy-rules-manage-bypass-request %}
            """;
        var beforeContext = DocsLiquidContext.Empty;
        var afterContext = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [reusableKey] = "Added reusable content.",
            });

        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/organizations/managing-organization-settings/governing-how-people-use-repositories-in-your-organization.md",
            markdown,
            "cedcbb7",
            "変更前",
            beforeContext,
            DocsVersion.Fpt,
            diffAgainstMarkdown: markdown,
            diffAgainstLiquidContext: afterContext,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);
        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/organizations/managing-organization-settings/governing-how-people-use-repositories-in-your-organization.md",
            markdown,
            "5c78377",
            "PR HEAD",
            afterContext,
            DocsVersion.Fpt,
            diffAgainstMarkdown: markdown,
            diffAgainstLiquidContext: beforeContext,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.DoesNotContain("repo-policy-rules-manage-bypass-request", beforeHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Added reusable content.", beforeHtml, StringComparison.Ordinal);
        Assert.Contains(
            "<span class=\"rsr-rendered-diff-added\">Added reusable content.</span>",
            afterHtml,
            StringComparison.Ordinal);
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
    public void Renders_Docs_Octicon_Tags_As_Inline_Svg()
    {
        var markdown = """
            ---
            title: Apply security configurations
            ---

            1. At the top of the page, click {% octicon "gear" aria-hidden="true" aria-label="gear" %} **Settings**.
            2. Select the **Apply to** {% octicon "triangle-down" aria-hidden="true" aria-label="triangle-down" %} dropdown menu.
            3. In the left sidebar, click {% octicon "codescan" aria-hidden="true" aria-label="codescan" %} **Code security**.
            4. Click {% octicon "organization" aria-hidden="true" aria-label="organization" %} **Organizations**, then {% octicon "kebab-horizontal" aria-label="More" %} **More**.
            5. From the dialog, click {% octicon "download" aria-hidden="true" %} **Download CSV**.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("<strong>Settings</strong>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-gear\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-triangle-down\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-codescan\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-organization\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-kebab-horizontal\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-download\"", html, StringComparison.Ordinal);
        Assert.Contains(".octicon{display:inline-block;vertical-align:text-bottom;fill:currentColor;overflow:visible;}", html, StringComparison.Ordinal);
        Assert.Contains("dropdown menu", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Download CSV</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% octicon", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-liquid", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Docs_Copilot_Octicon_Tags_As_Inline_Svg()
    {
        var markdown = """
            ---
            title: Configure Copilot policies
            ---

            4. In the sidebar, under "Code, planning, and automation", click {% octicon "copilot" aria-hidden="true" aria-label="copilot" %} **Copilot**, then click **Policies**.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/configure-copilot-policies.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("class=\"octicon octicon-copilot\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Copilot</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Policies</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% octicon \"copilot\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-liquid", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Docs_Alert_Octicon_Tag_As_Inline_Svg()
    {
        var markdown = """
            ---
            title: Sample
            ---

            6. From the results list, click {% octicon "alert" aria-hidden="true" aria-label="alert" %} **Failed reason**.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("class=\"octicon octicon-alert\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Failed reason</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% octicon \"alert\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-liquid", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Docs_Spotlight_Blocks_As_Official_Alert_Html()
    {
        var markdown = """
            ---
            title: Sample
            ---

            {% warning %}

            This is **important**.

            {% endwarning %}
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("class=\"ghd-alert ghd-alert-attention ghd-spotlight-attention\"", html, StringComparison.Ordinal);
        Assert.Contains("This is <strong>important</strong>.", html, StringComparison.Ordinal);
        Assert.Contains(".ghd-alert{", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% warning", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% endwarning", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-liquid", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_GitHub_Alert_Blockquotes_As_Alert_Html()
    {
        var markdown = """
            ---
            title: Sample
            ---

            > [!NOTE]
            > Useful information that users should know, even when skimming content.

            > [!TIP]
            > Helpful advice for doing things better or more easily.

            > [!IMPORTANT]
            > Key information users need to know to achieve their goal.

            > [!WARNING]
            > Urgent info that needs immediate user attention to avoid problems.

            > [!CAUTION]
            > Advises about risks or negative outcomes of certain actions.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-note\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-tip\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-important\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-warning\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-caution\"", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"ghd-markdown-alert-title\">Note</p>", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"ghd-markdown-alert-title\">Important</p>", html, StringComparison.Ordinal);
        Assert.Contains("Useful information that users should know", html, StringComparison.Ordinal);
        Assert.Contains("--rsr-alert-color:#8250df", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[!NOTE]", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[!CAUTION]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Inline_Alert_Marker_On_Same_Line_As_Alert_Html()
    {
        // github/docs では `> [!NOTE] {% data ... %}` のようにマーカーと本文が
        // 同じ行に並ぶことがある。これも NOTE アラートとして描画する。
        var markdown = """
            ---
            title: Sample
            ---

            > [!NOTE] Agent apps are currently in public preview and subject to change.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-note\"", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"ghd-markdown-alert-title\">Note</p>", html, StringComparison.Ordinal);
        Assert.Contains("Agent apps are currently in public preview and subject to change.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[!NOTE]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Inline_Alert_Body_When_Alert_Line_Is_Added()
    {
        const string beforeMarkdown = "Existing content.";
        const string afterMarkdown = "> [!NOTE] Agent apps are currently in public preview and subject to change.";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-note\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "<span class=\"rsr-rendered-diff-added\">Agent apps are currently in public preview and subject to change.</span>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[!NOTE]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Renders_Inline_Alert_Without_Space_After_Blockquote_Marker()
    {
        const string beforeMarkdown = "Existing content.";
        const string afterMarkdown = ">[!NOTE] Agent apps are currently in public preview and subject to change.";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-note\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "<span class=\"rsr-rendered-diff-added\">Agent apps are currently in public preview and subject to change.</span>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[!NOTE]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Does_Not_Split_Liquid_Tag_When_Marking_Rendered_Diff()
    {
        // 見出しの Liquid 変数が丸ごと差し替わったケース。差分マーカーの span が
        // {% ... %} タグの途中に挿入されるとタグが壊れる (タグ崩れ・id 破損) ので、
        // span はタグ全体を包み、タグ内部を分断しないこと。
        const string beforeMarkdown = "## {% data variables.product.prodname_github_apps %} and OAuth apps";
        const string afterMarkdown = "## {% data variables.copilot.agent_apps_caps %}";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        // Liquid タグが span で分断されていない (タグ内に span 終了タグが入らない)。
        Assert.Contains("{% data variables.copilot.agent_apps_caps %}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_apps_ca</span>", html, StringComparison.Ordinal);
        // 差分マーカー内に HTML エスケープされた span が混入していない。
        Assert.DoesNotContain("&lt;span class=&quot;rsr-rendered-diff", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Whole_Added_Heading_When_Only_Common_Suffix_Is_Unrelated()
    {
        const string beforeMarkdown = "## GitHub Apps and OAuth apps";
        const string afterMarkdown = "## Agent apps";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<span class=\"rsr-rendered-diff-added\">Agent apps</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\">Agent</span> apps", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Whole_Added_Sentence_When_Only_Common_Prefix_Is_Unrelated()
    {
        const string beforeMarkdown = "If the app requires additional configuration, the app will direct you to do so.";
        const string afterMarkdown = "If the app is installed in an organization owned by an enterprise, an administrator must also enable the policy before the agent features become available.";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<span class=\"rsr-rendered-diff-added\">If the app is installed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("If the app <span class=\"rsr-rendered-diff-added\">is installed", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Whole_Added_Sentence_When_Four_Boilerplate_Tokens_Match()
    {
        const string beforeMarkdown = "If the GitHub app displays an authentication prompt, follow the browser instructions to complete setup.";
        const string afterMarkdown = "If the GitHub app processes billing reports, enterprise owners can export monthly usage data.";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains(
            "<span class=\"rsr-rendered-diff-added\">" + afterMarkdown + "</span>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "If the GitHub app <span class=\"rsr-rendered-diff-added\">processes",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Whole_Inserted_Paragraph_When_It_Ends_With_Autotitle_Link()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/copilot/how-tos/administer-copilot/manage-for-enterprise/manage-agents/about-enterprise-plugin-standards"] = "About enterprise-managed plugin standards for Copilot CLI",
            });
        const string beforeMarkdown = """
            1. In your enterprise's `.github-private` repository, navigate to the `.github/copilot/` directory.
            """;
        const string afterMarkdown = """
            You can apply settings to control users' available plugin marketplaces and default-installed plugins. These settings apply to users on your enterprise's Copilot plan. For more information, see [AUTOTITLE](/copilot/how-tos/administer-copilot/manage-for-enterprise/manage-agents/about-enterprise-plugin-standards).

            1. In your enterprise's `.github-private` repository, navigate to the `.github/copilot/` directory.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/administer-copilot/manage-for-enterprise/manage-agents/configure-enterprise-plugin-standards.md",
            afterMarkdown,
            "23964e2",
            "PR HEAD",
            context,
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<span class=\"rsr-rendered-diff-added\">You can apply settings", html, StringComparison.Ordinal);
        Assert.Contains("About enterprise-managed plugin standards for Copilot CLI</a>.</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("You can apply settings to control users' available plugin marketplaces and default-installed plugins. <span class=\"rsr-rendered-diff-added\">These settings apply", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOTITLE", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Whole_Inserted_List_Items_When_Nearby_Item_Shares_Prefix()
    {
        const string beforeMarkdown = """
            ## European Union

            * GPT-4o mini
            * GPT-4.1
            * GPT-5 mini
            * GPT-5.2
            * GPT-5.3-Codex
            * GPT-5.4
            * Claude Haiku 4.5
            """;
        const string afterMarkdown = """
            ## European Union

            * GPT-4o mini
            * GPT-4.1
            * GPT-5 mini
            * GPT-5.2
            * GPT-5.3-Codex
            * GPT-5.4
            * GPT-5.4 mini
            * GPT-5.4 nano
            * GPT-5.5
            * Claude Haiku 4.5
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/reference/copilot-billing/models-and-pricing.md",
            afterMarkdown,
            "7bdd8fc",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<span class=\"rsr-rendered-diff-added\">GPT-5.4 mini</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\">GPT-5.4 nano</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\">GPT-5.5</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("GPT-5.4 <span class=\"rsr-rendered-diff-added\">mini</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("GPT-5.<span class=\"rsr-rendered-diff-added\">5</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_GitHub_Alert_Markers_In_Code_Fences_Untouched()
    {
        var markdown = """
            ---
            title: Sample
            ---

            ```markdown
            > [!NOTE]
            > This is an example, not an alert.
            ```
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("&gt; [!NOTE]", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"ghd-markdown-alert ghd-markdown-alert-note\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Changed_Code_Fence_Literals()
    {
        const string beforeMarkdown = """
            ## Step 2: send your first message

            ```typescript
            const client = new CopilotClient();
            const session = await client.createSession({ model: "gpt-4.1" });
            console.log(response?.data.content);
            ```
            """;
        const string afterMarkdown = """
            ## Step 2: send your first message

            ```typescript
            const client = new CopilotClient();
            const session = await client.createSession({ model: "auto" });
            console.log(response?.data.content);
            ```
            """;

        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/copilot-sdk/getting-started.md",
            beforeMarkdown,
            "6742a65-parent",
            "Parent",
            diffAgainstMarkdown: afterMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);
        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/copilot-sdk/getting-started.md",
            afterMarkdown,
            "6742a65",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);
        var beforeHtmlWithoutSyntax = RemoveSyntaxHighlightingMarkup(beforeHtml);
        var afterHtmlWithoutSyntax = RemoveSyntaxHighlightingMarkup(afterHtml);

        Assert.Contains("<span class=\"rsr-rendered-diff-removed\">gpt-4.1</span>", beforeHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\">auto</span>", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;span class=", beforeHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;span class=", afterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Does_Not_Mark_Code_Fence_Indentation_Only_Diffs()
    {
        const string beforeMarkdown = """
            1. Add your plugin policy configuration to the file.

               ```json copy
               {
                 "extraKnownMarketplaces": {
                   "MARKETPLACE-NAME": {
                     "source": {
                       "source": "github",
                       "repo": "OWNER/REPO"
                     }
                   }
                 },
                 "enabledPlugins": {
                   "PLUGIN-NAME@MARKETPLACE-NAME": true
                 }
               }
               ```
            """;
        const string afterMarkdown = """
            1. Add your plugin policy configuration to the file.

               ```json copy
                {
                  "extraKnownMarketplaces": {
                    "agent-skills": {
                      "source": {
                        "source": "github",
                        "repo": "OWNER/REPO"
                      }
                    }
                  },
                  "strictKnownMarketplaces": [
                    {
                      "source": "github",
                      "repo": "OWNER/REPO"
                    }
                  ],
                  "enabledPlugins": {
                    "PLUGIN-NAME@MARKETPLACE-NAME": true
                  }
                }
               ```
            """;

        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/administer-copilot/manage-for-enterprise/manage-agents/configure-enterprise-plugin-standards.md",
            beforeMarkdown,
            "34dbca9",
            "Parent",
            diffAgainstMarkdown: afterMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);
        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/administer-copilot/manage-for-enterprise/manage-agents/configure-enterprise-plugin-standards.md",
            afterMarkdown,
            "76a0870",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);
        var beforeHtmlWithoutSyntax = RemoveSyntaxHighlightingMarkup(beforeHtml);
        var afterHtmlWithoutSyntax = RemoveSyntaxHighlightingMarkup(afterHtml);

        Assert.Contains("MARKETPLACE-NAME", beforeHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-removed", beforeHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added rsr-rendered-diff-gap\"", beforeHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.Contains("agent-skills", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.Contains("strictKnownMarketplaces", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-added", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.Contains("<pre tabindex=\"0\"><code class=\"language-json\">", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.Contains("&quot;strictKnownMarketplaces&quot;: [", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\">&quot;source&quot;: &quot;github&quot;,</span>", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\">&quot;repo&quot;: &quot;OWNER/REPO&quot;</span>", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\">&quot;PLUGIN-NAME@MARKETPLACE-NAME&quot;: true</span>", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        var renamedMarketplaceStart = afterHtmlWithoutSyntax.IndexOf("&quot;agent-skills&quot;: {", StringComparison.Ordinal);
        var strictMarketplaceStart = afterHtmlWithoutSyntax.IndexOf("&quot;strictKnownMarketplaces&quot;: [", StringComparison.Ordinal);
        Assert.True(renamedMarketplaceStart >= 0);
        Assert.True(strictMarketplaceStart > renamedMarketplaceStart);
        var renamedMarketplaceHtml = afterHtmlWithoutSyntax[renamedMarketplaceStart..strictMarketplaceStart];
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\">&quot;source&quot;: {</span>", renamedMarketplaceHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\">&quot;source&quot;: &quot;github&quot;,</span>", renamedMarketplaceHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\">&quot;repo&quot;: &quot;OWNER/REPO&quot;</span>", renamedMarketplaceHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("</code></pre>\n</li>\n</ol>\n<p><span class=\"rsr-rendered-diff-added\">", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.DoesNotContain("<pre tabindex=\"0\"><code></code></pre>", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\"> </span>{", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\"> </span>}", afterHtmlWithoutSyntax, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-removed rsr-rendered-diff-gap\" aria-hidden=\"true\"></span>{", afterHtmlWithoutSyntax, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Restores_Code_Fence_Gap_Diff_Markers()
    {
        const string beforeMarkdown = """
            ```javascript
            runTask(input, options);
            ```
            """;
        const string afterMarkdown = """
            ```javascript
            runTask(input);
            ```
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("rsr-rendered-diff-removed rsr-rendered-diff-gap", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;span class=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;/span&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Wraps_Each_Code_Line_For_Preview_Alignment()
    {
        const string markdown = """
            ```typescript
            const first = 1;

            const second = 2;
            ```
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains(
            "<span class=\"rsr-code-line\"><span class=\"rsr-syntax-token\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "<span class=\"rsr-code-line\"><br></span>",
            html,
            StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(html, "<span class=\"rsr-code-line\">"));
        Assert.Contains(".rsr-code-line{display:block;min-height:1.55em;}", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Highlights_Added_CodeTab_Without_Escaping_Diff_Markers()
    {
        const string beforeMarkdown = """
            {% codetabs %}
            {% codetab csharp %}
            ```csharp
            var client = new CopilotClient();
            ```
            {% endcodetab %}
            {% endcodetabs %}
            """;
        const string afterMarkdown = """
            {% codetabs %}
            {% codetab csharp %}
            ```csharp
            var client = new CopilotClient();
            ```
            {% endcodetab %}
            {% codetab go %}
            ```golang
            package main

            import (
                "context"
                "github.com/Azure/azure-sdk-for-go/sdk/azidentity"
            )

            func main() {
                credential, err := azidentity.NewDefaultAzureCredential(nil)
                if err != nil {
                    log.Fatal(err)
                }
            }
            ```
            {% endcodetab %}
            {% endcodetabs %}
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/copilot-sdk/setup/azure-managed-identity.md",
            afterMarkdown,
            "129b085",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<pre tabindex=\"0\"><code class=\"language-golang\">", html, StringComparison.Ordinal);
        Assert.Contains("class=\"rsr-syntax-token\"", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\"><span class=\"rsr-syntax-token\"", html, StringComparison.Ordinal);
        Assert.Contains(">package</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;lt;span", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;span class=&quot;rsr-rendered-diff", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;span class=\"rsr-syntax-token\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>package main", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p><span class=\"rsr-rendered-diff-added\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<pre><code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Restores_Nested_Protected_Liquid_Blocks()
    {
        const string markdown = """
{% warning %}
{% codetabs %}
{% codetab csharp %}
```csharp
var value = 1;
```
{% endcodetab %}
{% endcodetabs %}
{% endwarning %}
""";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/example.md",
            markdown,
            "abc1234",
            "After");

        Assert.Contains("class=\"ghd-alert ghd-alert-attention ghd-spotlight-attention\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"ghd-code-tabs\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"rsr-syntax-token\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--rsr-protected-html:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_Source_Comments_That_Resemble_Placeholders()
    {
        const string markdown = """
<!--rsr-protected-html:0-->

{% codetabs %}
{% codetab csharp %}
```csharp
var value = 1;
```
{% endcodetab %}
{% endcodetabs %}
""";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/example.md",
            markdown,
            "abc1234",
            "After");

        Assert.Contains("<!--rsr-protected-html:0-->", html, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(html, "class=\"ghd-code-tabs\""));
    }

    [Fact]
    public void RenderDocument_Preserves_Literal_Span_Tags_In_Changed_Code()
    {
        const string beforeMarkdown = """
```markdown
<div><b>same</b></div>
```
""";
        const string afterMarkdown = """
```markdown
<div><span>new</span><b>same</b></div>
```
""";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/example.md",
            afterMarkdown,
            "abc1234",
            "After",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        var literalSpanOpen = html.IndexOf("&lt;span&gt;", StringComparison.Ordinal);
        var literalSpanClose = html.IndexOf("&lt;/span&gt;", StringComparison.Ordinal);
        var literalBoldOpen = html.IndexOf("&lt;b&gt;", StringComparison.Ordinal);
        Assert.True(literalSpanOpen >= 0);
        Assert.True(literalSpanClose > literalSpanOpen);
        Assert.True(literalBoldOpen > literalSpanClose);
        Assert.Contains("class=\"rsr-rendered-diff-added\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("RSR-CODE-DIFF:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_Mermaid_Fence_Rendering()
    {
        const string markdown = """
```mermaid
graph TD
    A --> B
```
""";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/example.md",
            markdown,
            "abc1234",
            "After");

        Assert.Contains("<pre class=\"mermaid\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<code class=\"language-mermaid\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_Changed_Mermaid_Fence_Rendering()
    {
        const string beforeMarkdown = """
```mermaid
graph TD
    A --> B
```
""";
        const string afterMarkdown = """
```mermaid
graph TD
    A --> C
```
""";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/example.md",
            afterMarkdown,
            "abc1234",
            "After",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<pre class=\"mermaid rsr-rendered-diff-added\">", html, StringComparison.Ordinal);
        Assert.Contains("A --&gt; C", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<code class=\"language-mermaid\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("RSR-CODE-DIFF:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_Fenced_Code_Attributes()
    {
        const string markdown = """
```csharp {#example .custom data-kind=sample}
var value = 1;
```
""";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/example.md",
            markdown,
            "abc1234",
            "After");

        var codeTagStart = html.IndexOf("<code", StringComparison.Ordinal);
        var codeTagEnd = html.IndexOf('>', codeTagStart);
        var codeTag = html[codeTagStart..(codeTagEnd + 1)];
        Assert.Contains("id=\"example\"", codeTag, StringComparison.Ordinal);
        Assert.Contains("language-csharp", codeTag, StringComparison.Ordinal);
        Assert.Contains("custom", codeTag, StringComparison.Ordinal);
        Assert.Contains("data-kind=\"sample\"", codeTag, StringComparison.Ordinal);
        Assert.Contains("class=\"rsr-syntax-token\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_GitHub_Alert_When_Diff_Marked()
    {
        const string beforeMarkdown = """
            ## Further reading

            Existing guidance.
            """;
        const string afterMarkdown = """
            ## Further reading

            > [!NOTE]
            > When you upgrade or switch your enterprise plan, your existing payment method is not carried forward.

            Existing guidance.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/organizations/managing-organization-settings/upgrading-to-the-github-customer-agreement.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-note\"", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"ghd-markdown-alert-title\">Note</p>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\">When you upgrade", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[!NOTE]", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&gt; [!NOTE]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_GitHub_Alert_List_Item_When_Diff_Marked()
    {
        const string beforeMarkdown = """
            > [!NOTE]
            > * Larger runners are always charged for.
            > * The storage amounts shown are shared with GitHub Packages.
            """;
        const string afterMarkdown = """
            > [!NOTE]
            > * Larger runners are always charged for.
            > * The storage amounts shown are shared with GitHub Packages.
            > * Copilot code review consumes GitHub Actions minutes on private repositories. For public repositories, GitHub Actions minutes remain free.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/billing/concepts/product-billing/github-actions.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-note\"", html, StringComparison.Ordinal);
        Assert.Contains("<li><span class=\"rsr-rendered-diff-added\">Copilot code review consumes GitHub Actions minutes on private repositories. For public repositories, GitHub Actions minutes remain free.</span></li>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p><span class=\"rsr-rendered-diff-added\">* Copilot code review", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_Asterisk_List_When_Diff_Marked()
    {
        const string beforeMarkdown = """
            ## Further reading

            * [Choosing a setup path](https://docs.github.com/copilot/setup)
            """;
        const string afterMarkdown = """
            ## Further reading

            * [Choosing a setup path](https://docs.github.com/copilot/setup)
            * [Understanding the agent loop](https://docs.github.com/copilot/agent-loop)
            * [Telemetry and observability](https://docs.github.com/copilot/telemetry)
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/copilot-sdk/sdk-getting-started.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<ul>", html, StringComparison.Ordinal);
        Assert.Contains("<li><span class=\"rsr-rendered-diff-added\"><a href=\"https://docs.github.com/copilot/agent-loop\">Understanding the agent loop</a></span></li>", html, StringComparison.Ordinal);
        Assert.Contains("<li><span class=\"rsr-rendered-diff-added\"><a href=\"https://docs.github.com/copilot/telemetry\">Telemetry and observability</a></span></li>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>*", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Expands_Link_Label_Diff_To_Added_Sentence()
    {
        const string beforeMarkdown = """
            Runs your workflow when you push a commit or tag, or when you create a repository from a template.

            A similar paragraph elsewhere can mention the same branch behavior. This includes workflows that are not merged into the default branch. For more information, see [Secure use reference](/actions/reference/workflows-and-actions/events-that-trigger-workflows#running-your-workflow-only-when-a-push-to-specific-branches-occurs).

            For more information, see [Events that trigger workflows](/actions/reference/workflows-and-actions/events-that-trigger-workflows).
            """;
        const string afterMarkdown = """
            Runs your workflow when you push a commit or tag, or when you create a repository from a template. This includes workflows that are not merged into the default branch. For more information, see [Events that trigger workflows](/actions/reference/workflows-and-actions/events-that-trigger-workflows#running-your-workflow-only-when-a-push-to-specific-branches-occurs).
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/actions/reference/workflows-and-actions/events-that-trigger-workflows.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<span class=\"rsr-rendered-diff-added\">This includes workflows", html, StringComparison.Ordinal);
        Assert.Contains("Events that trigger workflows</a>.</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a href=\"/actions/reference/workflows-and-actions/events-that-trigger-workflows#running-your-workflow-only-when-a-push-to-specific-branches-occurs\"><span class=\"rsr-rendered-diff-added\">Events that trigger workflows</span></a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Docs_Tool_Blocks_As_Official_Tool_Html()
    {
        var markdown = """
            ---
            title: Sample
            ---

            {% vscode %}

            Use the **Command Palette**.

            {% endvscode %}
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("class=\"ghd-tool vscode\"", html, StringComparison.Ordinal);
        Assert.Contains("Use the <strong>Command Palette</strong>.", html, StringComparison.Ordinal);
        Assert.Contains(".ghd-tool{", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% vscode", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% endvscode", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Docs_Prompt_Blocks_With_Copilot_Link_And_Icon()
    {
        var markdown = """
            ---
            title: Sample
            ---

            {% prompt %}Explain this function{% endprompt %}
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("Explain this function", html, StringComparison.Ordinal);
        Assert.Contains("https://github.com/copilot?prompt=Explain%20this%20function", html, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-copilot\"", html, StringComparison.Ordinal);
        Assert.Contains("copilot-prompt-long", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% prompt", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% endprompt", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Docs_CodeTabs_Blocks_Without_Liquid_Placeholders()
    {
        var markdown = """
            ---
            title: Sample
            ---

            {% codetabs %}
            {% codetab typescript %}
            ```typescript
            const client = new CopilotClient({
              useLoggedInUser: false,
            });
            ```
            {% endcodetab %}
            {% codetab python %}
            ```python
            client = CopilotClient({
                "use_logged_in_user": False,
            })
            ```
            {% endcodetab %}
            {% endcodetabs %}
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/how-tos/copilot-sdk/authentication.md",
            markdown,
            "abc1234",
            "PR HEAD");

        Assert.Contains("class=\"ghd-code-tabs\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"ghd-code-tab-label\">typescript</div>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"ghd-code-tab-label\">python</div>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"language-typescript\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"language-python\"", html, StringComparison.Ordinal);
        Assert.Contains("useLoggedInUser", html, StringComparison.Ordinal);
        Assert.Contains("use_logged_in_user", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% codetabs", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% codetab", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{% endcodetab", html, StringComparison.Ordinal);
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
    public void Rewrites_Local_Image_Assets_To_Preview_Asset_Routes()
    {
        var markdown = """
            ---
            title: Sample
            ---

            ![Settings tab](/assets/images/help/organizations/settings-tab.png)

            ![Local diagram](<images/local diagram.png>)

            <picture>
              <source srcset="/assets/images/help/organizations/settings-dark.png 1x, /assets/images/help/organizations/settings-dark@2x.png 2x" media="(prefers-color-scheme: dark)">
              <img src="../shared/light.png" alt="example">
            </picture>
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/code-security/how-to/secure-at-scale/configure-organization-security/page.md",
            markdown,
            "abc1234",
            "PR HEAD",
            assetBasePath: "/markdown-assets/after");

        Assert.Contains("src=\"/markdown-assets/after/assets/images/help/organizations/settings-tab.png\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"/markdown-assets/after/content/code-security/how-to/secure-at-scale/configure-organization-security/images/local%20diagram.png\"", html, StringComparison.Ordinal);
        Assert.Contains("srcset=\"/markdown-assets/after/assets/images/help/organizations/settings-dark.png 1x, /markdown-assets/after/assets/images/help/organizations/settings-dark%402x.png 2x\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"/markdown-assets/after/content/code-security/how-to/secure-at-scale/shared/light.png\"", html, StringComparison.Ordinal);
        Assert.Contains("img,video{max-width:100%;height:auto;}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"/assets/images", html, StringComparison.Ordinal);
        Assert.DoesNotContain("srcset=\"/assets/images", html, StringComparison.Ordinal);
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

    private static string RemoveSyntaxHighlightingMarkup(string html)
        => SyntaxHighlightingSpanRegex().Replace(html, "${body}");

    [GeneratedRegex(
        """<span class="rsr-syntax-token"[^>]*>(?<body>.*?)</span>""",
        RegexOptions.Singleline)]
    private static partial Regex SyntaxHighlightingSpanRegex();

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
    public void Version_Diff_Summary_Hides_Current_Version_And_Shows_Other_Versions()
    {
        // 表示中の版 (Ghec) の変更は本文のインライン差分で見えるため、
        // 変更パターンには出さず、他版限定の変更 (Fpt) だけを残す。
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
        Assert.Contains("Free, Pro, &amp; Team のみ", html, StringComparison.Ordinal);
        Assert.Contains("rsr-version-change-excerpt--removed", html, StringComparison.Ordinal);
        Assert.Contains("rsr-version-change-excerpt--added", html, StringComparison.Ordinal);
        Assert.Contains("text-decoration-line:line-through", html, StringComparison.Ordinal);
        // 表示中の版 (Ghec) の変更は本文で見えるため重複表示しない。
        Assert.DoesNotContain("Enterprise Cloud only addition", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rsr-version-diff-item--current", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rsr-version-current-chip", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_Diff_Summary_Omitted_When_All_Changes_Are_In_Current_Version()
    {
        // すべての差分が表示中の版に含まれる場合、本文のインライン差分と
        // 完全に重複するため、変更パターンのセクション自体を出さない。
        var sharedChange = new DocsVersionChangeSnippet(DocsVersionChangeKind.Updated, "Shared old note", "Shared updated note");
        var impacts = new[]
        {
            new DocsVersionImpactDetail(DocsVersion.Fpt, [sharedChange]),
            new DocsVersionImpactDetail(DocsVersion.Ghec, [sharedChange]),
        };

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            "# Hello",
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt, DocsVersion.Ghec],
            selectedVersion: DocsVersion.Ghec,
            versionImpacts: impacts);

        Assert.DoesNotContain("data-testid=\"rsr-version-diff-summary\"", html, StringComparison.Ordinal);
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

        // 表示中の版 (GHES 3.20) はどのグループにも含まれないため、
        // すべての変更パターンが他版限定として表示される。
        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            "# Hello",
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt, DocsVersion.Ghec, DocsVersion.Ghes("3.21")],
            selectedVersion: DocsVersion.Ghes("3.20"),
            versionImpacts: impacts);

        Assert.Contains("2 種類の他版限定の変更があります", html, StringComparison.Ordinal);
        Assert.Contains("変更パターン 1: 2 版で同じ変更", html, StringComparison.Ordinal);
        Assert.Contains("Free, Pro, &amp; Team", html, StringComparison.Ordinal);
        Assert.Contains("Enterprise Cloud", html, StringComparison.Ordinal);
        Assert.Contains("Enterprise Server 3.21 のみ", html, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(html, "<span class=\"rsr-version-change-excerpt--removed\""));
        Assert.Equal(2, CountOccurrences(html, "<span class=\"rsr-version-change-excerpt--added\""));
    }

    [Fact]
    public void Version_Diff_Summary_Rewrites_Autotitle_In_Excerpts()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/using-the-audit-log-api-for-your-enterprise"] = "Using the audit log API for your enterprise",
            });
        var impacts = new[]
        {
            new DocsVersionImpactDetail(
                DocsVersion.Ghec,
                [new DocsVersionChangeSnippet(
                    DocsVersionChangeKind.Added,
                    null,
                    "For more information, see [AUTOTITLE](/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/using-the-audit-log-api-for-your-enterprise).")]),
        };

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/exporting-audit-log-activity-for-your-enterprise.md",
            "# Exporting audit log activity",
            "abc1234",
            "PR HEAD",
            context,
            affectedVersions: [DocsVersion.Ghec],
            selectedVersion: DocsVersion.Fpt,
            versionImpacts: impacts);

        Assert.Contains("Using the audit log API for your enterprise", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOTITLE", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_Diff_Summary_Rewrites_Truncated_Autotitle_In_Excerpts()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/using-the-audit-log-api-for-your-enterprise"] = "Using the audit log API for your enterprise",
            });
        var impacts = new[]
        {
            new DocsVersionImpactDetail(
                DocsVersion.Ghec,
                [new DocsVersionChangeSnippet(
                    DocsVersionChangeKind.Added,
                    null,
                    "For more information, see [AUTOTITLE](/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/us...")]),
        };

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/exporting-audit-log-activity-for-your-enterprise.md",
            "# Exporting audit log activity",
            "abc1234",
            "PR HEAD",
            context,
            affectedVersions: [DocsVersion.Ghec],
            selectedVersion: DocsVersion.Fpt,
            versionImpacts: impacts);

        Assert.Contains("Using the audit log API for your enterprise", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOTITLE", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Rendered_Table_Row_Diffs_In_Full_File()
    {
        const string beforeMarkdown = """
            `issue` | `object` | The issue itself.
            """;
        const string afterMarkdown = """
            `issue` | `object` | The issue itself.
            `label` | `object` | The optional label.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "data/reusables/webhooks/issue_properties.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt],
            selectedVersion: DocsVersion.Fpt,
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<th>Name</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th>Type</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th>Description</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td><span class=\"rsr-rendered-diff-added\"><code>label</code></span></td>", html, StringComparison.Ordinal);
        Assert.Contains("<td><span class=\"rsr-rendered-diff-added\"><code>object</code></span></td>", html, StringComparison.Ordinal);
        Assert.Contains("<td><span class=\"rsr-rendered-diff-added\">The optional label.</span></td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p><code>issue</code> | <code>object</code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Only_Changed_Table_Cells_For_Existing_Row()
    {
        const string beforeMarkdown = """
            | Key | Type | Description |
            | --- | --- | --- |
            | `action` | `string` | The action that was performed. Can be one of `opened`, `closed`, `reopened`. |
            | `issue` | `object` | The issue itself. |
            """;
        const string afterMarkdown = """
            | Key | Type | Description |
            | --- | --- | --- |
            | `action` | `string` | The action that was performed. Can be one of `opened`, `closed`, `reopened`, `assigned`, `unassigned`, `labeled`, or `unlabeled`. |
            | `issue` | `object` | The issue itself. |
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/rest/using-the-rest-api/github-event-types.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<td><code>action</code></td>", html, StringComparison.Ordinal);
        Assert.Contains("<td><code>string</code></td>", html, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-added", html, StringComparison.Ordinal);
        Assert.Contains("<code>assigned</code>", html, StringComparison.Ordinal);
        Assert.Contains("<td><span class=\"rsr-rendered-diff-added\">The action that was performed.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\"><code>action</code></span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\"><code>string</code></span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;/span&gt;", html, StringComparison.Ordinal);
        Assert.Contains("<td><code>issue</code></td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\">The issue itself.</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Adds_Scrollbar_Markers_For_Rendered_Diffs()
    {
        const string beforeMarkdown = """
            Intro.
            """;
        const string afterMarkdown = """
            Intro.

            Added guidance.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("rsr-diff-scrollbar", html, StringComparison.Ordinal);
        Assert.Contains("rsr-diff-scrollbar-marker", html, StringComparison.Ordinal);
        Assert.Contains("right:0", html, StringComparison.Ordinal);
        Assert.Contains("width:10px", html, StringComparison.Ordinal);
        Assert.Contains(".rsr-rendered-diff-added,.rsr-rendered-diff-removed", html, StringComparison.Ordinal);
        Assert.Contains("document.querySelectorAll('[data-rsr-diff-navigation-index]')", html, StringComparison.Ordinal);
        Assert.Contains("groups.set(navigationIndex, { elements: [], removed: false })", html, StringComparison.Ordinal);
        Assert.Contains("element.closest(structuralBlockSelector)", html, StringComparison.Ordinal);
        Assert.Contains("window[stateKey] = { scheduleBuild }", html, StringComparison.Ordinal);
        Assert.Contains("marker.style.top", html, StringComparison.Ordinal);
        Assert.Contains("const docHeight = Math.max(1, document.documentElement.scrollHeight)", html, StringComparison.Ordinal);
        Assert.Contains("const scrollbarSize = Math.max(0, window.innerWidth - document.documentElement.clientWidth)", html, StringComparison.Ordinal);
        Assert.Contains("const buttonSize = Math.min(scrollbarSize, viewport / 4)", html, StringComparison.Ordinal);
        Assert.Contains("const trackHeight = Math.max(1, viewport - buttonSize * 2)", html, StringComparison.Ordinal);
        Assert.Contains("const absTop = Math.min(...rects.map(rect => rect.top)) + scrollY", html, StringComparison.Ordinal);
        Assert.Contains("const absBottom = Math.max(...rects.map(rect => rect.bottom)) + scrollY", html, StringComparison.Ordinal);
        Assert.Contains("const center = (absTop + absBottom) / 2 / docHeight", html, StringComparison.Ordinal);
        Assert.Contains("trackTop + center * trackHeight - height / 2", html, StringComparison.Ordinal);
        Assert.Contains("markerTop.toFixed(1)", html, StringComparison.Ordinal);
        Assert.Contains("marker.style.height", html, StringComparison.Ordinal);
        Assert.Contains("let buildPending = false", html, StringComparison.Ordinal);
        Assert.Contains("if (buildPending) return", html, StringComparison.Ordinal);
        Assert.Contains("buildPending = false", html, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('resize', scheduleBuild, { passive: true })", html, StringComparison.Ordinal);
        Assert.DoesNotContain("window.addEventListener('scroll'", html, StringComparison.Ordinal);
        Assert.Contains("window.setTimeout(scheduleBuild, 250)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Wraps_Code_Blocks_That_Contain_A_Diff()
    {
        const string beforeMarkdown = """
            Intro.

            ```ts
            const session = await client.createSession({ model: "gpt-4.1" });
            ```
            """;
        const string afterMarkdown = """
            Intro.

            ```ts
            const session = await client.createSession({ model: "auto" });
            ```
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        // A changed token inside a horizontally scrolling code block would be clipped
        // off the right edge of the narrow comparison pane, hiding the diff. Only the
        // code blocks that actually contain a diff must wrap so the change stays visible.
        Assert.Contains(
            "pre:has(.rsr-rendered-diff-added,.rsr-rendered-diff-removed){white-space:pre-wrap;overflow-wrap:anywhere;}",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_AriaHidden_On_Restored_Code_Fence_Gap_Marker()
    {
        const string beforeMarkdown = """
            Intro.

            ```ts
            const session = await client.createSession({ model: "auto", temperature: 0.2 });
            ```
            """;
        const string afterMarkdown = """
            Intro.

            ```ts
            const session = await client.createSession({ model: "auto" });
            ```
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        // The gap marker emitted inside a code fence is HTML-escaped by Markdig and
        // later restored. The restored marker must keep aria-hidden so screen readers
        // do not announce the decorative gap, and no escaped span must leak as text.
        Assert.Contains("rsr-rendered-diff-gap", html, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;span class=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;/span&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Highlights_Added_GitHub_Alert_Body()
    {
        const string beforeMarkdown = """
            # Test with pytest

            Existing guidance.
            """;
        const string afterMarkdown = """
            # Test with pytest

            > [!TIP]
            > This example already produces a Cobertura XML coverage report (`--cov-report=xml`). To display coverage results directly on pull requests, upload the report using the `actions/upload-code-coverage` action.

            Existing guidance.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/actions/tutorials/build-and-test-code/python.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("class=\"ghd-markdown-alert ghd-markdown-alert-tip\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"rsr-rendered-diff-added\"", html, StringComparison.Ordinal);
        Assert.Contains("This example already produces a Cobertura XML coverage report", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Strikes_Through_Removed_Rendered_Diffs()
    {
        const string beforeMarkdown = """
            Intro.

            Removed guidance.
            """;
        const string afterMarkdown = """
            Intro.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            beforeMarkdown,
            "abc1234",
            "Parent",
            diffAgainstMarkdown: afterMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);

        Assert.Contains("text-decoration-line:line-through", html, StringComparison.Ordinal);
        Assert.Contains("text-decoration-skip-ink:none", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-removed\">Removed guidance.</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_Added_Footnote_Definitions_When_Diff_Marked()
    {
        const string beforeMarkdown = """
            Claude Sonnet 4.6 remains available to individual Copilot subscribers.
            """;
        const string afterMarkdown = """
            Claude Sonnet 4.6 remains available to individual Copilot subscribers.[^claude-sonnet-46-annual]

            [^claude-sonnet-46-annual]: Claude Sonnet 4.6 remains available to individual Copilot subscribers on annual plans.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/reference/ai-models/supported-models.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("class=\"footnote-ref\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"footnotes\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "<span class=\"rsr-rendered-diff-added\">Claude Sonnet 4.6 remains available to individual Copilot subscribers on annual plans.</span>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[^claude-sonnet-46-annual]:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_Footnote_Definitions_Without_Whitespace_After_Colon()
    {
        const string markdown = """
            Text with a footnote.[^note]

            [^note]:Footnote text without leading whitespace.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            markdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: "Text without a footnote.",
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("class=\"footnote-ref\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"footnotes\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "<span class=\"rsr-rendered-diff-added\">Footnote text without leading whitespace.</span>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[^note]:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Only_Changed_Text_In_Updated_Paragraph()
    {
        const string unchangedPrefix = "Optionally, you can require review or approval from specific teams when a pull request changes certain files or directories. You can specify up to 15 different teams, and for each team you can require a certain number of approvals from team members.";
        const string addedSuffix = " For an approval from a team member to count, the team must have write permissions (or higher) for the repository.";
        var beforeMarkdown = unchangedPrefix;
        var afterMarkdown = unchangedPrefix + addedSuffix;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains(unchangedPrefix, html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\">" + addedSuffix + "</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"rsr-rendered-diff-added\">" + unchangedPrefix, html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Leaves_Shared_Text_Unmarked_In_Heavily_Rewritten_Paragraphs()
    {
        const string sharedSuffix = " You can do this in either of the following ways:";
        const string beforeMarkdown = """
            By default, autopilot mode applies only to the current task. Once Copilot determines that the task is complete, Copilot CLI automatically switches back to the standard interactive mode. To run another task in autopilot mode, press Shift+Tab and cycle through the available modes until you re-enter autopilot mode, then enter your next prompt.

            If you regularly run several tasks in autopilot mode, you can configure the CLI to stay in autopilot mode after each task completes, by enabling the `stayInAutopilot` setting. You can do this in either of the following ways:
            """;
        const string afterMarkdown = """
            By default, autopilot mode is sticky: once Copilot determines that a task is complete, Copilot CLI remains in autopilot mode, so the next prompt you enter is also handled in autopilot mode. You can switch back to the standard interactive mode at any time by pressing Shift+Tab.

            If you'd rather have Copilot CLI automatically switch back to interactive mode after each task completes, you can disable this behavior by setting `stayInAutopilot` to `false`. You can do this in either of the following ways:
            """;

        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/concepts/agents/copilot-cli/autopilot.md",
            afterMarkdown,
            "ff0cf46",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);
        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/concepts/agents/copilot-cli/autopilot.md",
            beforeMarkdown,
            "bd4da98",
            "Parent",
            diffAgainstMarkdown: afterMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);

        Assert.Contains(
            "<p>By default, autopilot mode <span class=\"rsr-rendered-diff-added\">is sticky:",
            afterHtml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<p>By default, autopilot mode <span class=\"rsr-rendered-diff-removed\">applies only",
            beforeHtml,
            StringComparison.Ordinal);
        Assert.Contains(sharedSuffix, afterHtml, StringComparison.Ordinal);
        Assert.Contains(sharedSuffix, beforeHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<p><span class=\"rsr-rendered-diff-added\">By default, autopilot mode",
            afterHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<p><span class=\"rsr-rendered-diff-removed\">By default, autopilot mode",
            beforeHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            sharedSuffix + "</span>",
            afterHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            sharedSuffix + "</span>",
            beforeHtml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Prefers_Stronger_Content_Matches_When_Changed_List_Items_Are_Reordered()
    {
        const string beforeMarkdown = """
            * GitHub Copilot supports Alpha workflows for repository owners in public repositories.
            * GitHub Copilot supports Beta workflows for organization owners in private repositories.
            """;
        const string afterMarkdown = """
            * GitHub Copilot supports Beta workflows for enterprise organization owners in private repositories.
            * GitHub Copilot supports Alpha workflows for repository owners in public and private repositories.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains(
            "<li>GitHub Copilot supports Beta workflows for <span class=\"rsr-rendered-diff-added\">enterprise </span>organization owners in private repositories.</li>",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "<li>GitHub Copilot supports Alpha workflows for repository owners in public <span class=\"rsr-rendered-diff-added\">and private </span>repositories.</li>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GitHub Copilot supports <span class=\"rsr-rendered-diff-added\">Beta",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Marks_Gap_When_After_Removes_End_Of_Paragraph()
    {
        const string beforeMarkdown = "Metered billing explanations. For more information, see [Billing cycles](/billing/concepts/billing-cycles).";
        const string afterMarkdown = "Metered billing explanations.";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/actions/how-tos/get-support.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("rsr-rendered-diff-gap", html, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-removed rsr-rendered-diff-gap", html, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"rendered diff gap\"", html, StringComparison.Ordinal);
        Assert.Contains("Metered billing explanations.<span", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Does_Not_Show_Raw_Diff_Spans_Inside_Inline_Code()
    {
        const string beforeMarkdown = "`@github` Create a PR for the widget function.";
        const string afterMarkdown = "`/delegate` Create a PR for the widget function.";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/copilot/tutorials/roll-out-at-scale/enable-developers/index.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.DoesNotContain("&lt;span class=&quot;rsr-rendered-diff-added&quot;&gt;", html, StringComparison.Ordinal);
        Assert.Contains("<code><span class=\"rsr-rendered-diff-added\">/delegate</span></code> Create a PR", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Does_Not_Show_Raw_Closing_Diff_Span_Across_Inline_Code()
    {
        const string beforeMarkdown = "* Supported for `bundler`, `composer`, `mix`, `maven`, `npm`, and `pip`.";
        const string afterMarkdown = "* Supported for `bundler`, `composer`, `mix`, `maven`, `npm`, `pip`, and `uv`.";

        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/code-security/tutorials/secure-your-dependencies/customizing-dependabot-prs.md",
            afterMarkdown,
            "b70c56f",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);
        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            "content/code-security/tutorials/secure-your-dependencies/customizing-dependabot-prs.md",
            beforeMarkdown,
            "4eaabd4",
            "Parent",
            diffAgainstMarkdown: afterMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);

        Assert.Contains(
            "<code><span class=\"rsr-rendered-diff-added\">pip</span></code><span class=\"rsr-rendered-diff-added\">, and </span><code><span class=\"rsr-rendered-diff-added\">uv</span></code>.",
            afterHtml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<span class=\"rsr-rendered-diff-removed\">and </span><code><span class=\"rsr-rendered-diff-removed\">pip</span></code>.",
            beforeHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;/span&gt;", afterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;/span&gt;", beforeHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Preserves_Added_H3_Heading_When_Diff_Marked()
    {
        const string beforeMarkdown = """
            ## Networking troubleshooting suggestions

            Existing guidance.
            """;
        const string afterMarkdown = """
            ## Networking troubleshooting suggestions

            Existing guidance.

            ### Runner IP addresses flagged by security scanners

            GitHub-hosted runners use shared infrastructure.
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/actions/how-tos/troubleshoot-workflows.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<h3", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"rsr-rendered-diff-added\">Runner IP addresses flagged by security scanners</span></h3>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("### Runner IP addresses flagged by security scanners", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Expands_Concatenated_Table_Row_Fragments()
    {
        const string beforeMarkdown = "`issue` | `object` | The issue itself. || `assignee` | `object` | The optional user.";
        const string afterMarkdown = "`issue` | `object` | The issue itself. || `assignee` | `object` | The optional user. || `labels` | `array` | The optional labels.";

        var html = MarkdownPreviewRenderer.RenderDocument(
            "data/reusables/webhooks/issue_properties.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<th>Name</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td><code>issue</code></td>", html, StringComparison.Ordinal);
        Assert.Contains("<td><code>object</code></td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>The issue itself.</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td><span class=\"rsr-rendered-diff-added\"><code>labels</code></span></td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p><code>issue</code> | <code>object</code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Expands_TrailingPipe_Table_Row_Fragments()
    {
        const string beforeMarkdown = """
            `issue` | `object` | The issue itself. |
            `assignee` | `object` | The optional user who was assigned or unassigned from the issue. |
            """;
        const string afterMarkdown = """
            `issue` | `object` | The issue itself. |
            `assignee` | `object` | The optional user who was assigned or unassigned from the issue. |
            `labels` | `array` | The optional array of label objects describing the labels on the issue. |
            """;

        var html = MarkdownPreviewRenderer.RenderDocument(
            "data/reusables/webhooks/issue_properties.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<th>Name</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td><code>issue</code></td>", html, StringComparison.Ordinal);
        Assert.Contains("<td><span class=\"rsr-rendered-diff-added\"><code>labels</code></span></td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p><code>issue</code> | <code>object</code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_Diff_Summary_Does_Not_Render_Table_Excerpts_As_Partial_Tables()
    {
        var impacts = new[]
        {
            new DocsVersionImpactDetail(
                DocsVersion.Fpt,
                [new DocsVersionChangeSnippet(
                    DocsVersionChangeKind.Updated,
                    "issue | object | The issue itself. || assignee | object | The optional user who was assigned or unassigned from the issue.",
                    "issue | object | The issue itself. || assignee | object | The optional user who was assigned or unassigned from the issue. | label | object | The optional label.")]),
        };

        var html = MarkdownPreviewRenderer.RenderDocument(
            "data/reusables/webhooks/issue_properties.md",
            "# Issue properties",
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt],
            selectedVersion: DocsVersion.Ghec,
            versionImpacts: impacts);

        Assert.Contains("本文のレンダリング済み差分で確認してください", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rsr-version-change-table", html, StringComparison.Ordinal);
        Assert.DoesNotContain("issue | object", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_Diff_Summary_Renders_Ifversion_And_Related_Feature_Changes()
    {
        var sourceDiff = new MarkdownSourceDiffSummary(
            [new MarkdownIfversionChange(DocsVersionChangeKind.Removed, "disable-ghas-button", null, "### Disable access\nUsers without a license cannot enable Advanced Security.")],
            [new MarkdownRelatedSourceFileChange(
                "data/features/disable-ghas-button.yml",
                [new MarkdownSourceLineChange(DocsVersionChangeKind.Updated, "  ghes: '>= 3.21'", "  ghes: '>= 3.22'")])]);

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/billing/how-tos/products/manage-ghas-licenses.md",
            "# Managing volume licenses",
            "abc1234",
            "PR HEAD",
            affectedVersions: [],
            sourceDiff: sourceDiff);

        Assert.Contains("data-testid=\"rsr-source-diff\"", html, StringComparison.Ordinal);
        Assert.Contains("レンダリングに出ないソース差分", html, StringComparison.Ordinal);
        Assert.Contains("{% ifversion disable-ghas-button %}", html, StringComparison.Ordinal);
        Assert.Contains("対象本文", html, StringComparison.Ordinal);
        Assert.Contains("### Disable access", html, StringComparison.Ordinal);
        Assert.Contains("Users without a license cannot enable Advanced Security.", html, StringComparison.Ordinal);
        Assert.Contains("data/features/disable-ghas-button.yml", html, StringComparison.Ordinal);
        Assert.Contains("&gt;= 3.21", html, StringComparison.Ordinal);
        Assert.Contains("&gt;= 3.22", html, StringComparison.Ordinal);
        Assert.Contains("本文レンダリング差分はありません", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_Diff_Summary_Rewrites_Autotitle_In_Excerpts()
    {
        var context = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/streaming-the-audit-log-for-your-enterprise"] = "Streaming the audit log for your enterprise",
            });
        var sourceDiff = new MarkdownSourceDiffSummary(
            [new MarkdownIfversionChange(
                DocsVersionChangeKind.Added,
                null,
                "ghec",
                null,
                "For more information, see [AUTOTITLE](/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/streaming-the-audit-log-for-your-enterprise).")],
            []);

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/exporting-audit-log-activity-for-your-enterprise.md",
            "# Exporting audit log activity",
            "abc1234",
            "PR HEAD",
            context,
            sourceDiff: sourceDiff);

        Assert.Contains("Streaming the audit log for your enterprise", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOTITLE", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_Diff_Summary_Hides_Ifversion_Change_When_Current_Version_Renders_It()
    {
        // {% ifversion fpt or ghec %} の追加は ghec の本文に出る（本文差分側で見える）ため、
        // ghec 表示中はソース差分セクションに出してはならない。
        var sourceDiff = new MarkdownSourceDiffSummary(
            [new MarkdownIfversionChange(
                DocsVersionChangeKind.Added,
                null,
                "fpt or ghec",
                null,
                "Powered by the cloud agent, you can trigger these agents.")],
            []);

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/apps/using-github-apps/about-using-github-apps.md",
            "# About using GitHub Apps",
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt, DocsVersion.Ghec],
            selectedVersion: DocsVersion.Ghec,
            sourceDiff: sourceDiff);

        Assert.DoesNotContain("data-testid=\"rsr-source-diff\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("レンダリングに出ないソース差分", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_Diff_Summary_Shows_Ifversion_Change_When_Current_Version_Does_Not_Render_It()
    {
        // 同じ {% ifversion fpt or ghec %} の追加でも、ghes 3.21 表示中は本文に出ないため、
        // 見落とし防止のためソース差分セクションに残す。
        var sourceDiff = new MarkdownSourceDiffSummary(
            [new MarkdownIfversionChange(
                DocsVersionChangeKind.Added,
                null,
                "fpt or ghec",
                null,
                "Powered by the cloud agent, you can trigger these agents.")],
            []);

        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/apps/using-github-apps/about-using-github-apps.md",
            "# About using GitHub Apps",
            "abc1234",
            "PR HEAD",
            affectedVersions: [DocsVersion.Fpt, DocsVersion.Ghec],
            selectedVersion: DocsVersion.Ghes("3.21"),
            sourceDiff: sourceDiff);

        Assert.Contains("data-testid=\"rsr-source-diff\"", html, StringComparison.Ordinal);
        Assert.Contains("{% ifversion fpt or ghec %}", html, StringComparison.Ordinal);
        Assert.Contains("表示中の版では本文に出ない", html, StringComparison.Ordinal);
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
