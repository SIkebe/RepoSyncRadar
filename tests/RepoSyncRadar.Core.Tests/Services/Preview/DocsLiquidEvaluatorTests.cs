using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="DocsLiquidEvaluator"/> — the minimal Liquid
/// interpreter used by the Markdown-first preview path
/// (IMPLEMENTATION_PLAN.md §Step 19.8). Only documents the subset of
/// github/docs tags we promise to expand; everything else stays as-is so
/// <see cref="MarkdownPreviewRenderer"/> can wrap it in a placeholder span.
/// </summary>
public sealed class DocsLiquidEvaluatorTests
{
    private static DocsLiquidContext WithVariables(params (string Key, string Value)[] pairs)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
        {
            vars[k] = v;
        }
        return new DocsLiquidContext(vars, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static DocsLiquidContext WithReusables(params (string Key, string Value)[] pairs)
    {
        var reusables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
        {
            reusables[k] = v;
        }
        return new DocsLiquidContext(new Dictionary<string, string>(StringComparer.Ordinal), reusables);
    }

    [Fact]
    public void Returns_Empty_For_Null_Input()
    {
        Assert.Equal(string.Empty, DocsLiquidEvaluator.Evaluate(null, DocsLiquidContext.Empty));
    }

    [Fact]
    public void Returns_Source_Unchanged_When_No_Liquid_Tags()
    {
        const string source = "Hello world. No tags here.";
        var result = DocsLiquidEvaluator.Evaluate(source, DocsLiquidContext.Empty);
        Assert.Equal(source, result);
    }

    [Fact]
    public void Expands_Single_Variable()
    {
        var ctx = WithVariables(("product.prodname_copilot", "GitHub Copilot"));

        var result = DocsLiquidEvaluator.Evaluate(
            "Try {% data variables.product.prodname_copilot %} today.",
            ctx);

        Assert.Equal("Try GitHub Copilot today.", result);
    }

    [Fact]
    public void Expands_Variable_Interpolation_Curly_Braces()
    {
        var ctx = WithVariables(("product.prodname", "GitHub"));

        var result = DocsLiquidEvaluator.Evaluate(
            "Welcome to {{ variables.product.prodname }}!",
            ctx);

        Assert.Equal("Welcome to GitHub!", result);
    }

    [Fact]
    public void Expands_Site_Data_Variable_Interpolation()
    {
        var ctx = WithVariables(("product.prodname", "GitHub"));

        var result = DocsLiquidEvaluator.Evaluate(
            "Welcome to {{ site.data.variables.product.prodname }}!",
            ctx);

        Assert.Equal("Welcome to GitHub!", result);
    }

    [Fact]
    public void Leaves_Unknown_Variable_Tag_Unchanged_For_Span_Wrapping_Later()
    {
        var result = DocsLiquidEvaluator.Evaluate(
            "Hello {% data variables.unknown.key %}.",
            DocsLiquidContext.Empty);

        Assert.Equal("Hello {% data variables.unknown.key %}.", result);
    }

    [Fact]
    public void Expands_Reusable_Recursively_With_Nested_Variable()
    {
        var ctx = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["product.prodname_copilot"] = "GitHub Copilot",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["copilot.about"] = "Try {% data variables.product.prodname_copilot %} today.",
            });

        var result = DocsLiquidEvaluator.Evaluate(
            "{% data reusables.copilot.about %}",
            ctx);

        Assert.Equal("Try GitHub Copilot today.", result);
    }

    [Fact]
    public void Expands_Reusable_With_Plus_Argument_By_Base_Key()
    {
        var ctx = WithReusables(
            ("advanced-security.ghas-products-bullets", "* Secret Protection\n* Code Security"));

        var result = DocsLiquidEvaluator.Evaluate(
            "{% data reusables.advanced-security.ghas-products-bullets+ghas %}",
            ctx);

        Assert.Equal("* Secret Protection\n* Code Security", result);
    }

    [Fact]
    public void Trims_Data_Tag_Expansion_Like_Official_Docs()
    {
        var ctx = WithReusables(("guide.note", "\nUse this note.\n"));

        var result = DocsLiquidEvaluator.Evaluate(
            "{% data reusables.guide.note %}",
            ctx);

        Assert.Equal("Use this note.", result);
    }

    [Fact]
    public void Preserves_Data_Tag_Indent_For_Multiline_Reusables()
    {
        var ctx = WithReusables(("guide.steps", "first\nsecond"));

        var result = DocsLiquidEvaluator.Evaluate(
            "1. Do the thing\n   {% data reusables.guide.steps %}",
            ctx);

        Assert.Equal("1. Do the thing\n   first\n   second", result);
    }

    [Fact]
    public void Preserves_Data_Tag_Blockquote_Context_For_Multiline_Reusables()
    {
        var ctx = WithReusables(("guide.note", "first\nsecond"));

        var result = DocsLiquidEvaluator.Evaluate(
            "> {% data reusables.guide.note %}",
            ctx);

        Assert.Equal("> first\n> second", result);
    }

    [Fact]
    public void Collapses_Extra_Blank_Lines_Left_By_Liquid_Evaluation()
    {
        var result = DocsLiquidEvaluator.Evaluate(
            "before\n\n\n{% ifversion fpt %}after{% endif %}",
            DocsLiquidContext.Empty,
            DocsVersion.Fpt);

        Assert.Equal("before\n\nafter", result);
    }

    [Fact]
    public void Evaluates_Ifversion_Introduced_By_Variable_Expansion()
    {
        var ctx = WithVariables(
            ("product.choice", "{% ifversion fpt %}Free{% else %}Enterprise{% endif %}"));

        var fpt = DocsLiquidEvaluator.Evaluate(
            "Plan: {% data variables.product.choice %}.",
            ctx,
            DocsVersion.Fpt);
        var ghec = DocsLiquidEvaluator.Evaluate(
            "Plan: {% data variables.product.choice %}.",
            ctx,
            DocsVersion.Ghec);

        Assert.Equal("Plan: Free.", fpt);
        Assert.Equal("Plan: Enterprise.", ghec);
    }

    [Fact]
    public void Evaluates_Ifversion_Introduced_By_Reusable_Expansion()
    {
        var ctx = WithReusables(
            ("guide.note", "{% ifversion ghec %}Cloud{% else %}Server{% endif %}"));

        var result = DocsLiquidEvaluator.Evaluate(
            "{% data reusables.guide.note %}",
            ctx,
            DocsVersion.Ghec);

        Assert.Equal("Cloud", result);
    }

    [Fact]
    public void Renders_Octicon_Tags_As_Primer_Svg()
    {
        var result = DocsLiquidEvaluator.Evaluate(
            "Click {% octicon \"gear\" aria-hidden=\"true\" aria-label=\"gear\" %} Settings.",
            DocsLiquidContext.Empty);

        Assert.Contains("Click <svg", result, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-gear\"", result, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"gear\"", result, StringComparison.Ordinal);
        Assert.Contains("role=\"img\"", result, StringComparison.Ordinal);
        Assert.Contains("data-component=\"Octicon\"", result, StringComparison.Ordinal);
        Assert.Contains("Settings.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-hidden", result, StringComparison.Ordinal);
        Assert.DoesNotContain("{% octicon", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Security_Configuration_Octicons_As_Primer_Svg()
    {
        var result = DocsLiquidEvaluator.Evaluate(
            "Click {% octicon \"organization\" aria-hidden=\"true\" aria-label=\"organization\" %} Organizations, then {% octicon \"kebab-horizontal\" aria-label=\"More\" %} More.",
            DocsLiquidContext.Empty);

        Assert.Contains("class=\"octicon octicon-organization\"", result, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-kebab-horizontal\"", result, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"organization\"", result, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"More\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("{% octicon", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_Octicon_Tags_Introduced_By_Reusable_Expansion()
    {
        var ctx = WithReusables(
            ("admin.apply", "Apply to {% octicon \"triangle-down\" aria-hidden=\"true\" aria-label=\"triangle-down\" %} dropdown."));

        var result = DocsLiquidEvaluator.Evaluate(
            "{% data reusables.admin.apply %}",
            ctx);

        Assert.Contains("Apply to <svg", result, StringComparison.Ordinal);
        Assert.Contains("class=\"octicon octicon-triangle-down\"", result, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"triangle-down\"", result, StringComparison.Ordinal);
        Assert.Contains("dropdown.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("{% octicon", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Ifversion_Without_Else_Returns_Body_When_Condition_True()
    {
        var result = DocsLiquidEvaluator.Evaluate(
            "{% ifversion fpt %}only{% endif %}",
            DocsLiquidContext.Empty,
            DocsVersion.Fpt);

        Assert.Equal("only", result);
    }

    [Fact]
    public void Ifversion_Without_Else_Returns_Empty_When_Condition_False()
    {
        var result = DocsLiquidEvaluator.Evaluate(
            "{% ifversion fpt %}only{% endif %}",
            DocsLiquidContext.Empty,
            DocsVersion.Ghec);

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("fpt", "A")]
    [InlineData("ghec", "B")]
    [InlineData("ghes-3.21", "C")]
    public void Ifversion_With_Elsif_And_Else_Selects_Correct_Branch_Per_Version(string slug, string expected)
    {
        var version = DocsVersionCatalog.FromSlug(slug);

        var result = DocsLiquidEvaluator.Evaluate(
            "{% ifversion fpt %}A{% elsif ghec %}B{% else %}C{% endif %}",
            DocsLiquidContext.Empty,
            version);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("ghes-3.18", "old")]
    [InlineData("ghes-3.20", "new")]
    [InlineData("ghes-3.21", "new")]
    public void Ifversion_With_Ghes_Comparison_Picks_Branch_By_Release(string slug, string expected)
    {
        var version = DocsVersionCatalog.FromSlug(slug);

        var result = DocsLiquidEvaluator.Evaluate(
            "{% ifversion ghes < 3.20 %}old{% else %}new{% endif %}",
            DocsLiquidContext.Empty,
            version);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void If_Tag_Returns_First_Branch_Only_Regardless_Of_Version()
    {
        // {% if X %} (= 版に非依存の Liquid 条件) はフル評価器を持たないため
        // 互換のため最初の分岐を採用する。
        var result = DocsLiquidEvaluator.Evaluate(
            "before {% if foo %}yes{% else %}no{% endif %} after",
            DocsLiquidContext.Empty,
            DocsVersion.Fpt);

        Assert.Equal("before yes after", result);
    }

    [Fact]
    public void Resolves_Nested_Ifversion_With_Version_Aware_Branches()
    {
        // 外側 fpt で fpt = true → 外側 first branch を採用。
        // 内側 ghec で fpt のとき false → inner-B (else)。
        var result = DocsLiquidEvaluator.Evaluate(
            "{% ifversion fpt %}outer-A {% ifversion ghec %}inner-A{% else %}inner-B{% endif %} outer-Z{% else %}other{% endif %}",
            DocsLiquidContext.Empty,
            DocsVersion.Fpt);

        Assert.Equal("outer-A inner-B outer-Z", result);
    }

    [Fact]
    public void Resolves_Nested_Ifversion_When_Outer_Falls_To_Else()
    {
        // 外側 fpt で ghes 評価 → false → else 採用 → "other"。
        var result = DocsLiquidEvaluator.Evaluate(
            "{% ifversion fpt %}outer-A {% ifversion ghec %}inner-A{% else %}inner-B{% endif %} outer-Z{% else %}other{% endif %}",
            DocsLiquidContext.Empty,
            DocsVersion.Ghes("3.21"));

        Assert.Equal("other", result);
    }

    [Fact]
    public void Unknown_Feature_Flag_In_Ifversion_Keeps_Body_Visible()
    {
        // {% ifversion copilot-feature %} のような不明識別子は保守的に true 扱い。
        // body が消えずレビュアーが差分を見落とさない。
        var result = DocsLiquidEvaluator.Evaluate(
            "{% ifversion copilot-feature %}new feature note{% endif %}",
            DocsLiquidContext.Empty,
            DocsVersion.Fpt);

        Assert.Equal("new feature note", result);
    }

    [Fact]
    public void Raw_Block_Protects_Inner_Liquid_From_Evaluation()
    {
        var ctx = WithVariables(("product.prodname", "GitHub"));

        var result = DocsLiquidEvaluator.Evaluate(
            "Outside {% data variables.product.prodname %} | {% raw %}{% data variables.product.prodname %}{% endraw %}",
            ctx);

        // Outside the raw block: expanded. Inside: kept literal.
        Assert.Equal(
            "Outside GitHub | {% data variables.product.prodname %}",
            result);
    }

    [Fact]
    public void Indented_Data_Reference_Adds_Spaces_Prefix_To_Each_Line()
    {
        var ctx = WithReusables(("guide.steps", "step 1\nstep 2\nstep 3"));

        var result = DocsLiquidEvaluator.Evaluate(
            "{% indented_data_reference reusables.guide.steps spaces=4 %}",
            ctx);

        Assert.Equal("    step 1\n    step 2\n    step 3", result);
    }

    [Fact]
    public void Indented_Data_Reference_Without_Spaces_Returns_Body_As_Is()
    {
        var ctx = WithReusables(("guide.steps", "step 1\nstep 2"));

        var result = DocsLiquidEvaluator.Evaluate(
            "{% indented_data_reference reusables.guide.steps %}",
            ctx);

        Assert.Equal("step 1\nstep 2", result);
    }

    [Fact]
    public void Recursive_Expansion_Halts_At_Max_Depth_Without_Stack_Overflow()
    {
        // Cycle: A → B → A. The evaluator must not loop forever.
        var ctx = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a"] = "{% data reusables.b %}",
                ["b"] = "{% data reusables.a %}",
            });

        var result = DocsLiquidEvaluator.Evaluate("{% data reusables.a %}", ctx, maxRecursionDepth: 3);

        // We don't constrain the exact terminal token — only that the call
        // returned in bounded time without throwing or hanging.
        Assert.NotNull(result);
    }

    [Fact]
    public void Leaves_Unsupported_Tags_Like_Link_Unchanged()
    {
        // {% link /foo %} is github/docs specific and not in our MVP; the
        // evaluator should pass it through so NeutralizeLiquid can wrap it.
        var result = DocsLiquidEvaluator.Evaluate(
            "See {% link /copilot/about %} for more.",
            DocsLiquidContext.Empty);

        Assert.Equal("See {% link /copilot/about %} for more.", result);
    }
}
