using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

public sealed class MarkdownSourceChangeAnalyzerTests
{
    [Fact]
    public void Detects_Liquid_Variable_Reference_Replacement()
    {
        const string before = """
            Before you update a ruleset, confirm that {% data variables.code-quality.workflow_name_actions %} runs.
            """;
        const string after = """
            Before you update a ruleset, confirm that {% data variables.product.prodname_code_quality_short %} runs.
            """;

        var summary = MarkdownSourceChangeAnalyzer.Analyze(before, after);

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.LiquidVariableReference, summary!.Kind);
        Assert.Equal("code-quality.workflow_name_actions", summary.Before);
        Assert.Equal("product.prodname_code_quality_short", summary.After);
        Assert.Equal(1, summary.ChangeCount);
    }

    [Theory]
    [InlineData(
        "{{ variables.code-quality.workflow_name_actions }}",
        "{{ variables.product.prodname_code_quality_short }}")]
    [InlineData(
        "{{ site.data.variables.code-quality.workflow_name_actions }}",
        "{{ site.data.variables.product.prodname_code_quality_short }}")]
    public void Detects_Liquid_Interpolation_Reference_Replacement(string beforeReference, string afterReference)
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(
            $"The workflow is {beforeReference}.",
            $"The workflow is {afterReference}.");

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.LiquidVariableReference, summary!.Kind);
        Assert.Equal("code-quality.workflow_name_actions", summary.Before);
        Assert.Equal("product.prodname_code_quality_short", summary.After);
        Assert.Equal(1, summary.ChangeCount);
    }

    [Fact]
    public void Detects_Filtered_Liquid_Interpolation_Reference_Replacement()
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(
            """The value is {{ variables.old | default: "x" }}.""",
            """The value is {{ variables.new | default: "x" }}.""");

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.LiquidVariableReference, summary!.Kind);
        Assert.Equal("old", summary.Before);
        Assert.Equal("new", summary.After);
        Assert.Equal(1, summary.ChangeCount);
    }

    [Fact]
    public void Does_Not_Classify_Filter_Changes_As_Liquid_Reference_Changes()
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(
            """The value is {{ variables.old | default: "old" }}.""",
            """The value is {{ variables.new | default: "new" }}.""");

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.SourceOnly, summary!.Kind);
    }

    [Fact]
    public void Does_Not_Skip_Filter_Changes_On_Otherwise_Unchanged_References()
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(
            """{{ variables.a | default: "old" }} {{ variables.old }}""",
            """{{ variables.a | default: "new" }} {{ variables.new }}""");

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.SourceOnly, summary!.Kind);
    }

    [Theory]
    [InlineData("{% data variables.old-%}", "{% data variables.new-%}")]
    [InlineData("{{ variables.old-}}", "{{ variables.new-}}")]
    public void Excludes_Liquid_Whitespace_Control_Markers_From_Reference_Keys(
        string before,
        string after)
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(before, after);

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.LiquidVariableReference, summary!.Kind);
        Assert.Equal("old", summary.Before);
        Assert.Equal("new", summary.After);
    }

    [Theory]
    [InlineData(null, "{% data variables.empty_value %}")]
    [InlineData("{% data variables.empty_value %}", null)]
    public void Detects_Added_Or_Removed_Liquid_Variable_Reference(
        string? before,
        string? after)
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(before, after);

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.LiquidVariableReference, summary!.Kind);
        Assert.Equal(before is null ? null : "empty_value", summary.Before);
        Assert.Equal(after is null ? null : "empty_value", summary.After);
        Assert.Equal(1, summary.ChangeCount);
    }

    [Fact]
    public void Detects_Frontmatter_Only_Change()
    {
        const string before = """
            ---
            title: Old title
            ---

            Same body.
            """;
        const string after = """
            ---
            title: New title
            ---

            Same body.
            """;

        var summary = MarkdownSourceChangeAnalyzer.Analyze(before, after);

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.Frontmatter, summary!.Kind);
        Assert.Equal("title: Old title", summary.Before);
        Assert.Equal("title: New title", summary.After);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Detects_Frontmatter_Only_Change_When_Closing_Delimiter_Is_At_Eof(string newline)
    {
        var before = $"---{newline}title: Old title{newline}---";
        var after = $"---{newline}title: New title{newline}---";

        var summary = MarkdownSourceChangeAnalyzer.Analyze(before, after);

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.Frontmatter, summary!.Kind);
        Assert.Equal("title: Old title", summary.Before);
        Assert.Equal("title: New title", summary.After);
    }

    [Fact]
    public void Detects_Added_Metadata_Only_File_As_Frontmatter()
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(
            null,
            "---\ntitle: New title\n---");

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.Frontmatter, summary!.Kind);
        Assert.Null(summary.Before);
        Assert.Equal("title: New title", summary.After);
    }

    [Fact]
    public void Returns_Null_When_Only_Line_Endings_Change()
    {
        Assert.Null(MarkdownSourceChangeAnalyzer.Analyze(
            "First line.\r\nSecond line.",
            "First line.\nSecond line."));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", null)]
    public void Preserves_Missing_Versus_ZeroByte_File(string? before, string? after)
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(before, after);

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.SourceOnly, summary!.Kind);
        Assert.Equal(before, summary.Before);
        Assert.Equal(after, summary.After);
        Assert.Equal(1, summary.ChangeCount);
    }

    [Fact]
    public void Detects_Frontmatter_When_Body_Only_Changes_Line_Endings()
    {
        const string before = "---\ntitle: Old title\n---\nFirst line.\r\nSecond line.";
        const string after = "---\ntitle: New title\n---\nFirst line.\nSecond line.";

        var summary = MarkdownSourceChangeAnalyzer.Analyze(before, after);

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.Frontmatter, summary!.Kind);
        Assert.Equal("title: Old title", summary.Before);
        Assert.Equal("title: New title", summary.After);
    }

    [Theory]
    [InlineData("---\ntitle: Same \n---\nBody.", "---\ntitle: Same\n---\nBody.", "title: Same ", "title: Same")]
    [InlineData("---\ntitle: Same\n---\nBody.", "---\ntitle: Same\n\n---\nBody.", null, "")]
    public void Preserves_Frontmatter_Source_Differences_Normalized_By_The_Structured_Diff(
        string before,
        string after,
        string? expectedBefore,
        string? expectedAfter)
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(before, after);

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.Frontmatter, summary!.Kind);
        Assert.Equal(expectedBefore, summary.Before);
        Assert.Equal(expectedAfter, summary.After);
    }

    [Fact]
    public void Describes_First_Generic_Source_Only_Change()
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(
            "Same text.\n<!-- old note -->",
            "Same text.\n<!-- new note -->");

        Assert.NotNull(summary);
        Assert.Equal(MarkdownSourceChangeKind.SourceOnly, summary!.Kind);
        Assert.Equal("<!-- old note -->", summary.Before);
        Assert.Equal("<!-- new note -->", summary.After);
    }

    [Fact]
    public void Centers_Long_Source_Excerpts_On_First_Difference()
    {
        var prefix = new string('a', 157);
        var summary = MarkdownSourceChangeAnalyzer.Analyze(
            prefix + "old ending",
            prefix + "new ending");

        Assert.NotNull(summary);
        Assert.NotEqual(summary!.Before, summary.After);
        Assert.Contains("old ending", summary.Before, StringComparison.Ordinal);
        Assert.Contains("new ending", summary.After, StringComparison.Ordinal);
        Assert.True(summary.Before!.Length <= 160);
        Assert.True(summary.After!.Length <= 160);
    }

    [Fact]
    public void Uses_Line_Diff_Hunk_When_Content_Is_Moved_And_Replaced()
    {
        var summary = MarkdownSourceChangeAnalyzer.Analyze(
            "Same\n<!-- old -->\nKeep",
            "Same\nKeep\n<!-- new -->");

        Assert.NotNull(summary);
        Assert.Equal("<!-- old -->", summary!.Before);
        Assert.Null(summary.After);
        Assert.DoesNotContain("Keep", summary.Before, StringComparison.Ordinal);
        Assert.Equal(1, summary.ChangeCount);
    }

    [Fact]
    public void Finds_First_Hunk_In_Large_Files_Without_A_Quadratic_Matrix()
    {
        var beforeLines = Enumerable.Range(0, 10_000)
            .Select(static index => $"Line {index}")
            .ToArray();
        var afterLines = (string[])beforeLines.Clone();
        beforeLines[5_000] = "<!-- old -->";
        afterLines[5_000] = "<!-- new -->";

        var summary = MarkdownSourceChangeAnalyzer.Analyze(
            string.Join('\n', beforeLines),
            string.Join('\n', afterLines));

        Assert.NotNull(summary);
        Assert.Equal("<!-- old -->", summary!.Before);
        Assert.Equal("<!-- new -->", summary.After);
        Assert.Equal(1, summary.ChangeCount);
    }

    [Fact]
    public void Counts_A_Large_Complete_Rewrite_In_Linear_Time()
    {
        var before = string.Join(
            '\n',
            Enumerable.Range(0, 10_000).Select(static index => $"Before {index}"));
        var after = string.Join(
            '\n',
            Enumerable.Range(0, 10_000).Select(static index => $"After {index}"));

        var summary = MarkdownSourceChangeAnalyzer.Analyze(before, after);

        Assert.NotNull(summary);
        Assert.Equal("Before 0", summary!.Before);
        Assert.Equal("After 0", summary.After);
        Assert.Equal(10_000, summary.ChangeCount);
    }

    [Fact]
    public void Returns_Null_For_Identical_Source()
    {
        Assert.Null(MarkdownSourceChangeAnalyzer.Analyze("Same.", "Same."));
    }
}
