using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="DocsVersionImpactAnalyzer"/> — detects which
/// <see cref="DocsVersion"/> entries see a different rendering between
/// the before and after Markdown so the preview UI can surface them as
/// "affected versions" badges (IMPLEMENTATION_PLAN.md §Step 19.9).
/// </summary>
public sealed class DocsVersionImpactAnalyzerTests
{
    [Fact]
    public void Returns_Empty_When_Both_Are_Identical()
    {
        const string md = "# Hello world\n\nUnchanged content.";

        var affected = DocsVersionImpactAnalyzer.Analyze(
            md,
            DocsLiquidContext.Empty,
            md,
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Detects_Relative_Autotitle_Label_Changes_Caused_By_Rename()
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

        var affected = DocsVersionImpactAnalyzer.AnalyzeDetails(
            markdown,
            context,
            markdown,
            context,
            "content/area/source.md",
            "content/area/sub/source.md");

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
        Assert.All(affected, detail =>
        {
            var change = Assert.Single(detail.Changes);
            Assert.Contains("Root target", change.BeforeExcerpt, StringComparison.Ordinal);
            Assert.Contains("Area target", change.AfterExcerpt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Returns_All_Versions_When_Plain_Text_Changes_Outside_Any_Ifversion()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "Original line.",
            DocsLiquidContext.Empty,
            "Edited line.",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
        Assert.True(DocsVersionImpactAnalyzer.IsAllVersionsAffected(affected));
    }

    [Fact]
    public void Returns_Empty_When_Only_NonRendered_Frontmatter_Changes()
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

        var affected = DocsVersionImpactAnalyzer.Analyze(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Returns_Empty_When_Only_Html_Comment_Changes()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "Same text.\n<!-- old note -->",
            DocsLiquidContext.Empty,
            "Same text.\n<!-- new note -->",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Theory]
    [InlineData("`<!-- old note -->`", "`<!-- new note -->`")]
    [InlineData("```html\n<!-- old note -->\n```", "```html\n<!-- new note -->\n```")]
    public void Detects_Html_Comment_Syntax_Changes_Inside_Code(string before, string after)
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Returns_Empty_When_Only_Collapsible_Text_Whitespace_Changes()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "Same text.",
            DocsLiquidContext.Empty,
            "Same  text.",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Returns_Empty_When_Only_Raw_Html_Tag_Spacing_Changes()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<span  title = "x">Same text.</span>""",
            DocsLiquidContext.Empty,
            """<span title="x">Same text.</span>""",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Analyze_Handles_A_Large_Rendered_Rewrite_Without_Building_Snippets()
    {
        var before = string.Join(
            "\n\n",
            Enumerable.Range(0, 10_000).Select(static index => $"Before paragraph {index}."));
        var after = string.Join(
            "\n\n",
            Enumerable.Range(0, 10_000).Select(static index => $"After paragraph {index}."));

        var affected = DocsVersionImpactAnalyzer.Analyze(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Theory]
    [InlineData("A&#32;B", "A B")]
    [InlineData("""<SPAN title='x'>Same text.</SPAN>""", """<span title="x">Same text.</span>""")]
    [InlineData("""<span TITLE=x>Same text.</span>""", """<span title="x">Same text.</span>""")]
    [InlineData("""<span title="A&#32;B">Same text.</span>""", """<span title="A B">Same text.</span>""")]
    [InlineData(
        """<span title="x" class="y">Same text.</span>""",
        """<span class="y" title="x">Same text.</span>""")]
    [InlineData("""<input disabled>""", """<input disabled="">""")]
    [InlineData("""<input disabled="false">""", """<input disabled>""")]
    [InlineData("""<div hidden="false">Same text.</div>""", """<div hidden>Same text.</div>""")]
    [InlineData("<textarea>A&#32;B</textarea>", "<textarea>A B</textarea>")]
    [InlineData("<textarea>&#128;</textarea>", "<textarea>€</textarea>")]
    [InlineData(
        "<textarea>&CounterClockwiseContourIntegral;</textarea>",
        "<textarea>∳</textarea>")]
    public void Returns_Empty_When_Only_Browser_Equivalent_Html_Syntax_Changes(
        string before,
        string after)
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Preserves_First_Duplicate_Html_Attribute_Value()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<span title="first" title="second">Same text.</span>""",
            DocsLiquidContext.Empty,
            """<span title="second" title="first">Same text.</span>""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Equates_Valueless_And_Empty_NonBoolean_Html_Attributes()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "<input value>",
            DocsLiquidContext.Empty,
            """<input value="">""",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Preserves_Hidden_UntilFound_Semantics()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<div hidden="until-found">Same text.</div>""",
            DocsLiquidContext.Empty,
            """<div hidden>Same text.</div>""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Ignores_Closing_Tags_For_Html_Void_Elements()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "Before <input></input> after",
            DocsLiquidContext.Empty,
            "Before <input> after",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Preserves_Entity_Syntax_Inside_Code()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "`A&#32;B`",
            DocsLiquidContext.Empty,
            "`A B`",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Does_Not_Equate_Html5_Numeric_Reference_With_Dotnet_Control_Character()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "<textarea>&#128;</textarea>",
            DocsLiquidContext.Empty,
            "<textarea>\u0080</textarea>",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Decodes_Character_References_Only_Once()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "<textarea>&#38;amp;</textarea>",
            DocsLiquidContext.Empty,
            "<textarea>&amp;amp;</textarea>",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Handles_Many_Invalid_Named_Entity_Prefixes_In_Linear_Time()
    {
        var invalidReferences = string.Concat(Enumerable.Repeat("&not-an-entity ", 10_000));

        var affected = DocsVersionImpactAnalyzer.Analyze(
            $"{invalidReferences}<!-- old -->",
            DocsLiquidContext.Empty,
            $"{invalidReferences}<!-- new -->",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Preserves_NonBreaking_Space_Differences()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "a b",
            DocsLiquidContext.Empty,
            "a\u00A0b",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Collapses_Whitespace_Inside_Inline_Code()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "`a  b`",
            DocsLiquidContext.Empty,
            "`a b`",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Preserves_Whitespace_Differences_Inside_Fenced_Code()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "```\na  b\n```",
            DocsLiquidContext.Empty,
            "```\na b\n```",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Treats_Br_As_A_Collapsible_Whitespace_Boundary()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "<span>a<br> b</span>",
            DocsLiquidContext.Empty,
            "<span>a<br>b</span>",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Preserves_Whitespace_Differences_Inside_Raw_Html_Attributes()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<input value="a  b">""",
            DocsLiquidContext.Empty,
            """<input value="a b">""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Preserves_Whitespace_When_Inline_Style_Disables_Collapsing()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<span style="white-space: pre">a  b</span>""",
            DocsLiquidContext.Empty,
            """<span style="white-space: pre">a b</span>""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Preserves_Whitespace_When_Inline_Style_Contains_Css_Comments()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<span style="white-space: pre /* keep */">a  b</span>""",
            DocsLiquidContext.Empty,
            """<span style="white-space: pre /* keep */">a b</span>""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Reads_Slashes_Inside_Unquoted_Html_Attribute_Values()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<span style=background:url(/x);white-space:pre>a  b</span>""",
            DocsLiquidContext.Empty,
            """<span style=background:url(/x);white-space:pre>a b</span>""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("nowrap")]
    public void Collapses_Whitespace_For_Collapsing_Inline_Styles(string whiteSpace)
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            $"""<span style="white-space: {whiteSpace}">a  b</span>""",
            DocsLiquidContext.Empty,
            $"""<span style="white-space: {whiteSpace}">a b</span>""",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Applies_Important_To_Recognized_WhiteSpace_Value()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<span style="white-space: normal !important">a  b</span>""",
            DocsLiquidContext.Empty,
            """<span style="white-space: normal !important">a b</span>""",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Theory]
    [InlineData("""<span style="white-space-collapse: preserve">a  b</span>""")]
    [InlineData("""<span style="white-space: preserve">a  b</span>""")]
    [InlineData("""<span style="--mode: pre; white-space: var(--mode)">a  b</span>""")]
    public void Preserves_Whitespace_For_Longhand_Or_Computed_Css(string before)
    {
        var after = before.Replace("a  b", "a b", StringComparison.Ordinal);

        var affected = DocsVersionImpactAnalyzer.Analyze(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Ignores_Invalid_WhiteSpaceCollapse_Value()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<pre style="white-space-collapse: nowrap">a  b</pre>""",
            DocsLiquidContext.Empty,
            """<pre style="white-space-collapse: nowrap">a b</pre>""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Important_WhiteSpace_Declaration_Beats_Later_NonImportant_Declaration()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<span style="white-space: pre !important; white-space: normal">a  b</span>""",
            DocsLiquidContext.Empty,
            """<span style="white-space: pre !important; white-space: normal">a b</span>""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Later_Important_WhiteSpace_Declaration_Wins()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<span style="white-space: pre !important; white-space: normal !important">a  b</span>""",
            DocsLiquidContext.Empty,
            """<span style="white-space: pre !important; white-space: normal !important">a b</span>""",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Ignores_Invalid_WhiteSpace_Value()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<pre style="white-space: normal-invalid">a  b</pre>""",
            DocsLiquidContext.Empty,
            """<pre style="white-space: normal-invalid">a b</pre>""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Preserves_Line_Breaks_But_Collapses_Spaces_For_PreLine()
    {
        var spaces = DocsVersionImpactAnalyzer.Analyze(
            """<span style="white-space: pre-line">a  b</span>""",
            DocsLiquidContext.Empty,
            """<span style="white-space: pre-line">a b</span>""",
            DocsLiquidContext.Empty);
        var lineBreak = DocsVersionImpactAnalyzer.Analyze(
            "<span style=\"white-space: pre-line\">a\r\nb</span>",
            DocsLiquidContext.Empty,
            """<span style="white-space: pre-line">a b</span>""",
            DocsLiquidContext.Empty);

        Assert.Empty(spaces);
        Assert.Equal(DocsVersionCatalog.All.Count, lineBreak.Count);
    }

    [Theory]
    [InlineData("preserve-breaks")]
    [InlineData("preserve-breaks nowrap")]
    public void Preserves_Line_Breaks_But_Collapses_Spaces_For_Modern_PreserveBreaks(
        string whiteSpace)
    {
        var spaces = DocsVersionImpactAnalyzer.Analyze(
            $"""<span style="white-space: {whiteSpace}">a  b</span>""",
            DocsLiquidContext.Empty,
            $"""<span style="white-space: {whiteSpace}">a b</span>""",
            DocsLiquidContext.Empty);
        var lineBreak = DocsVersionImpactAnalyzer.Analyze(
            $"<span style=\"white-space: {whiteSpace}\">a\r\nb</span>",
            DocsLiquidContext.Empty,
            $"""<span style="white-space: {whiteSpace}">a b</span>""",
            DocsLiquidContext.Empty);

        Assert.Empty(spaces);
        Assert.Equal(DocsVersionCatalog.All.Count, lineBreak.Count);
    }

    [Fact]
    public void Still_Collapses_Unrelated_Text_When_Page_Contains_Preformatted_Style()
    {
        const string fixedSpan = """<span style="white-space: pre">fixed</span>""";
        var affected = DocsVersionImpactAnalyzer.Analyze(
            $"{fixedSpan}\n\nSame text.",
            DocsLiquidContext.Empty,
            $"{fixedSpan}\n\nSame  text.",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Preserves_Whitespace_When_A_Stylesheet_May_Apply_WhiteSpace_Rules()
    {
        const string style = "<style>.preserve { white-space: pre }</style>";
        var affected = DocsVersionImpactAnalyzer.Analyze(
            $"{style}<span class=\"preserve\">a  b</span>",
            DocsLiquidContext.Empty,
            $"{style}<span class=\"preserve\">a b</span>",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Honors_Nested_WhiteSpace_Overrides()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<div style="white-space: normal"><span style="white-space: pre">a  b</span></div>""",
            DocsLiquidContext.Empty,
            """<div style="white-space: normal"><span style="white-space: pre">a b</span></div>""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Collapses_Whitespace_Across_Adjacent_Inline_Elements()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "<span>a </span><span>b</span>",
            DocsLiquidContext.Empty,
            "<span>a</span><span> b</span>",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Theory]
    [InlineData(
        """<span style="background:red">a </span><span>b</span>""",
        """<span style="background:red">a</span><span> b</span>""")]
    [InlineData(
        """<span>a </span><span style="background:red">b</span>""",
        """<span>a</span><span style="background:red"> b</span>""")]
    public void Preserves_Whitespace_Ownership_Across_Styled_Inline_Boundaries(
        string before,
        string after)
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Preserves_Whitespace_Ownership_Across_Inline_Code_Boundaries()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "<code>A </code>B",
            DocsLiquidContext.Empty,
            "<code>A</code> B",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Preserves_Whitespace_Ownership_Across_Link_Boundaries()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<a href="x">A </a>B""",
            DocsLiquidContext.Empty,
            """<a href="x">A</a> B""",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Preserves_Whitespace_Ownership_Across_Button_Boundaries()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "<button>A </button>B",
            DocsLiquidContext.Empty,
            "<button>A</button> B",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Preserves_Whitespace_After_Empty_Inline_Controls()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "<button></button>X",
            DocsLiquidContext.Empty,
            "<button></button> X",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Theory]
    [InlineData("strong")]
    [InlineData("em")]
    [InlineData("del")]
    [InlineData("mark")]
    [InlineData("sub")]
    [InlineData("sup")]
    public void Preserves_Whitespace_Ownership_Across_Semantic_Inline_Boundaries(
        string tagName)
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            $"<{tagName}>A </{tagName}>B",
            DocsLiquidContext.Empty,
            $"<{tagName}>A</{tagName}> B",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Stops_Analysis_When_Cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DocsVersionImpactAnalyzer.AnalyzeCancellable(
                "Before.",
                DocsLiquidContext.Empty,
                "After.",
                DocsLiquidContext.Empty,
                beforeRepoPath: null,
                afterRepoPath: null,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Preserves_Prompt_Whitespace_That_Changes_The_Copilot_Link()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "{% prompt %}Explain  this{% endprompt %}",
            DocsLiquidContext.Empty,
            "{% prompt %}Explain this{% endprompt %}",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Equates_Concatenated_And_Separate_Table_Row_Fragments()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "`issue` | `object` | The issue itself. || `assignee` | `object` | The optional user.",
            DocsLiquidContext.Empty,
            "`issue` | `object` | The issue itself. |\n`assignee` | `object` | The optional user. |",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Fact]
    public void Ignores_Comments_Inside_Styled_Elements()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            """<span style="white-space: pre"><!-- old --></span>""",
            DocsLiquidContext.Empty,
            """<span style="white-space: pre"><!-- new --></span>""",
            DocsLiquidContext.Empty);

        Assert.Empty(affected);
    }

    [Theory]
    [InlineData("""<input value="<!-- old -->">""", """<input value="<!-- new -->">""")]
    [InlineData("<textarea><!-- old --></textarea>", "<textarea><!-- new --></textarea>")]
    public void Preserves_Comment_Syntax_In_Html_Text_And_Attributes(string before, string after)
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Restores_Nested_Whitespace_Sensitive_Elements()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "<pre><textarea><!-- old --></textarea></pre>",
            DocsLiquidContext.Empty,
            "<pre><textarea><!-- new --></textarea></pre>",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Reports_Rendering_Change_When_Indented_Code_Becomes_Paragraph()
    {
        var details = DocsVersionImpactAnalyzer.AnalyzeDetails(
            "    code",
            DocsLiquidContext.Empty,
            "code",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, details.Count);
        Assert.All(details, detail => Assert.NotEmpty(detail.Changes));
    }

    [Fact]
    public void Flags_Only_Fpt_When_Change_Lives_Inside_Ifversion_Fpt_Block()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "{% ifversion fpt %}old fpt note{% endif %}",
            DocsLiquidContext.Empty,
            "{% ifversion fpt %}new fpt note{% endif %}",
            DocsLiquidContext.Empty);

        var slugs = affected.Select(v => v.Slug).ToArray();
        Assert.Single(slugs);
        Assert.Equal("fpt", slugs[0]);
    }

    [Fact]
    public void Flags_Only_Ghec_When_Change_Lives_Inside_Ifversion_Ghec_Block()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "{% ifversion ghec %}old ghec note{% endif %}",
            DocsLiquidContext.Empty,
            "{% ifversion ghec %}new ghec note{% endif %}",
            DocsLiquidContext.Empty);

        var slugs = affected.Select(v => v.Slug).ToArray();
        Assert.Single(slugs);
        Assert.Equal("ghec", slugs[0]);
    }

    [Fact]
    public void Flags_All_Ghes_Versions_When_Change_Is_Inside_Generic_Ghes_Block()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "{% ifversion ghes %}old{% endif %}",
            DocsLiquidContext.Empty,
            "{% ifversion ghes %}new{% endif %}",
            DocsLiquidContext.Empty);

        var slugs = affected.Select(v => v.Slug).ToArray();
        Assert.All(slugs, slug => Assert.StartsWith("ghes-", slug, StringComparison.Ordinal));
        Assert.Equal(DocsVersionCatalog.GhesReleases.Count, slugs.Length);
    }

    [Fact]
    public void Flags_Only_Older_Ghes_When_Change_Is_Inside_Comparison_Block()
    {
        // before/after で {% ifversion ghes < 3.20 %}...{% endif %} の body が変わる場合、
        // 3.20 未満の ghes だけが影響を受ける (= 3.16-3.19)。
        var affected = DocsVersionImpactAnalyzer.Analyze(
            "{% ifversion ghes < 3.20 %}old text{% endif %}",
            DocsLiquidContext.Empty,
            "{% ifversion ghes < 3.20 %}new text{% endif %}",
            DocsLiquidContext.Empty);

        var slugs = affected.Select(v => v.Slug).ToArray();
        Assert.DoesNotContain("fpt", slugs);
        Assert.DoesNotContain("ghec", slugs);
        Assert.DoesNotContain("ghes-3.20", slugs);
        Assert.DoesNotContain("ghes-3.21", slugs);
        Assert.Contains("ghes-3.18", slugs);
        Assert.Contains("ghes-3.19", slugs);
    }

    [Fact]
    public void Flags_Both_Plans_When_Two_Independent_Ifversion_Blocks_Change()
    {
        // ユーザの要望ど真ん中: fpt と ghec で別々の変更があるケース。
        // fpt 側 と ghec 側 両方が影響リストに出ることを検証する。
        var before = "Common header.\n{% ifversion fpt %}fpt-old{% endif %}\n{% ifversion ghec %}ghec-old{% endif %}";
        var after = "Common header.\n{% ifversion fpt %}fpt-NEW{% endif %}\n{% ifversion ghec %}ghec-NEW{% endif %}";

        var affected = DocsVersionImpactAnalyzer.Analyze(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        var slugs = affected.Select(v => v.Slug).ToArray();
        Assert.Contains("fpt", slugs);
        Assert.Contains("ghec", slugs);
    }

    [Fact]
    public void AnalyzeDetails_Separates_Fpt_And_Ghec_Change_Text()
    {
        var before = "Common header.\n{% ifversion fpt %}Free old note{% endif %}\n{% ifversion ghec %}Cloud old note{% endif %}";
        var after = "Common header.\n{% ifversion fpt %}Free updated note{% endif %}\n{% ifversion ghec %}Cloud updated note{% endif %}";

        var details = DocsVersionImpactAnalyzer.AnalyzeDetails(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        var fpt = Assert.Single(details, detail => detail.Version == DocsVersion.Fpt);
        var ghec = Assert.Single(details, detail => detail.Version == DocsVersion.Ghec);
        var fptChange = Assert.Single(fpt.Changes);
        var ghecChange = Assert.Single(ghec.Changes);
        Assert.Equal(DocsVersionChangeKind.Updated, fptChange.Kind);
        Assert.Contains("Free old note", fptChange.BeforeExcerpt, StringComparison.Ordinal);
        Assert.Contains("Free updated note", fptChange.AfterExcerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("Cloud", fptChange.AfterExcerpt, StringComparison.Ordinal);
        Assert.Equal(DocsVersionChangeKind.Updated, ghecChange.Kind);
        Assert.Contains("Cloud old note", ghecChange.BeforeExcerpt, StringComparison.Ordinal);
        Assert.Contains("Cloud updated note", ghecChange.AfterExcerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("Free", ghecChange.AfterExcerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeDetails_Reports_Ghec_Only_Additions()
    {
        var before = "Common header.";
        var after = "Common header.\n\n{% ifversion ghec %}Enterprise Cloud only addition.{% endif %}";

        var details = DocsVersionImpactAnalyzer.AnalyzeDetails(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        var detail = Assert.Single(details);
        Assert.Equal(DocsVersion.Ghec, detail.Version);
        var change = Assert.Single(detail.Changes);
        Assert.Equal(DocsVersionChangeKind.Added, change.Kind);
        Assert.Null(change.BeforeExcerpt);
        Assert.Contains("Enterprise Cloud only addition", change.AfterExcerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_Before_Is_Treated_As_Add_To_All_Versions()
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            beforeMarkdown: null,
            DocsLiquidContext.Empty,
            "New doc body.",
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Different_Liquid_Contexts_With_Identical_Markdown_Are_Detected()
    {
        // 同じ Markdown でも変数定義が変わると展開結果が変わる → 全版で影響あり。
        var beforeCtx = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["product.name"] = "Old" },
            new Dictionary<string, string>(StringComparer.Ordinal));
        var afterCtx = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["product.name"] = "New" },
            new Dictionary<string, string>(StringComparer.Ordinal));

        var affected = DocsVersionImpactAnalyzer.Analyze(
            "Hello {% data variables.product.name %}!",
            beforeCtx,
            "Hello {% data variables.product.name %}!",
            afterCtx);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
    }

    [Fact]
    public void Feature_Gated_Removal_Is_Detected_For_Ghes_Versions_Where_Feature_Is_Disabled()
    {
        var afterContext = new DocsLiquidContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["enhanced-billing-platform"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["fpt"] = "*",
                    ["ghec"] = "*",
                    ["ghes"] = ">= 3.22",
                },
            });

        var details = DocsVersionImpactAnalyzer.AnalyzeDetails(
            "Metered billing explanations. For more information, see [AUTOTITLE](/billing/concepts/billing-cycles).",
            DocsLiquidContext.Empty,
            "Metered billing explanations. {% ifversion enhanced-billing-platform %}For more information, see [AUTOTITLE](/billing/concepts/billing-cycles).{% endif %}",
            afterContext);

        var slugs = details.Select(static detail => detail.Version.Slug).ToArray();
        Assert.DoesNotContain("fpt", slugs);
        Assert.DoesNotContain("ghec", slugs);
        Assert.Contains("ghes-3.21", slugs);
        Assert.Contains("ghes-3.16", slugs);
    }

    [Fact]
    public void AnalyzeDetails_Ignores_Blank_Line_Removed_After_Heading()
    {
        // 見出し直後の空行が削除されただけ（fpt/ghec 限定の ifversion ブロック追加に
        // 伴う整形差）。GHES では本文レンダリングは変わらないので差分なしと判定すべき。
        var before = "## GitHub Apps and OAuth apps\n\nGitHub also supports OAuth apps. Unlike GitHub Apps, you do not install an OAuth app.";
        var after = "{% ifversion fpt or ghec %}\n\n## Agents\n\nAgent intro.\n\n{% endif %}\n\n## GitHub Apps and OAuth apps\nGitHub also supports OAuth apps. Unlike GitHub Apps, you do not install an OAuth app.";

        var details = DocsVersionImpactAnalyzer.AnalyzeDetails(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        var slugs = details.Select(static detail => detail.Version.Slug).ToArray();
        // fpt/ghec は Agents セクション追加という実質差分があるので含まれる。
        Assert.Contains("fpt", slugs);
        Assert.Contains("ghec", slugs);
        // GHES は見出し後の空行が消えただけ → 差分なし。
        Assert.DoesNotContain("ghes-3.21", slugs);
        Assert.DoesNotContain("ghes-3.16", slugs);
    }
}
