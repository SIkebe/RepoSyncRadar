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
        Assert.Contains("rsr-version-change-excerpt--removed", html, StringComparison.Ordinal);
        Assert.Contains("rsr-version-change-excerpt--added", html, StringComparison.Ordinal);
        Assert.Contains("text-decoration-line:line-through", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rsr-version-change-excerpt--removed\">Free old note", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rsr-version-change-excerpt--added\">Free updated note", html, StringComparison.Ordinal);
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
            selectedVersion: DocsVersion.Ghec,
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
            selectedVersion: DocsVersion.Ghec,
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
        Assert.Contains("right:24px", html, StringComparison.Ordinal);
        Assert.Contains("width:10px", html, StringComparison.Ordinal);
        Assert.Contains(".rsr-rendered-diff-added,.rsr-rendered-diff-removed", html, StringComparison.Ordinal);
        Assert.Contains("const blockSelector = 'p,li,h1,h2,h3,h4,h5,h6,td,th,blockquote,.ghd-markdown-alert'", html, StringComparison.Ordinal);
        Assert.Contains("element.closest(blockSelector) || element", html, StringComparison.Ordinal);
        Assert.Contains("marker.style.top", html, StringComparison.Ordinal);
        Assert.Contains("const documentHeight = Math.max(1, root.scrollHeight)", html, StringComparison.Ordinal);
        Assert.Contains("const scrollTop = root.scrollTop || window.scrollY || 0", html, StringComparison.Ordinal);
        Assert.Contains("const documentTop = Math.max(0, rect.top + scrollTop)", html, StringComparison.Ordinal);
        Assert.Contains("documentTop / documentHeight", html, StringComparison.Ordinal);
        Assert.Contains("rect.height / documentHeight", html, StringComparison.Ordinal);
        Assert.Contains("viewportHeight - height", html, StringComparison.Ordinal);
        Assert.Contains("markerTop.toFixed(1)", html, StringComparison.Ordinal);
        Assert.Contains("marker.style.height", html, StringComparison.Ordinal);
        Assert.Contains("window.setTimeout(scheduleBuild, 250)", html, StringComparison.Ordinal);
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
            selectedVersion: DocsVersion.Fpt,
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
