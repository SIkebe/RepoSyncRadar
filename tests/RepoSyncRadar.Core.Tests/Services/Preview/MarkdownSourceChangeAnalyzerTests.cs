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
    public void Returns_Null_For_Identical_Source()
    {
        Assert.Null(MarkdownSourceChangeAnalyzer.Analyze("Same.", "Same."));
    }
}
