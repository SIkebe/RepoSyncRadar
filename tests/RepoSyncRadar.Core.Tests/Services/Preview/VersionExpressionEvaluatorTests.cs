using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="VersionExpressionEvaluator"/> — parses and evaluates
/// <c>{% ifversion ... %}</c> expressions against a concrete
/// <see cref="DocsVersion"/> (IMPLEMENTATION_PLAN.md §Step 19.9).
/// </summary>
public sealed class VersionExpressionEvaluatorTests
{
    [Theory]
    [InlineData("fpt", true)]
    [InlineData("ghec", false)]
    [InlineData("ghes", false)]
    [InlineData("ghae", false)]
    public void Identifier_Matches_Plan_Fpt(string expression, bool expected)
    {
        Assert.Equal(expected, VersionExpressionEvaluator.Evaluate(expression, DocsVersion.Fpt));
    }

    [Theory]
    [InlineData("fpt", false)]
    [InlineData("ghec", true)]
    [InlineData("ghes", false)]
    public void Identifier_Matches_Plan_Ghec(string expression, bool expected)
    {
        Assert.Equal(expected, VersionExpressionEvaluator.Evaluate(expression, DocsVersion.Ghec));
    }

    [Theory]
    [InlineData("fpt", false)]
    [InlineData("ghec", false)]
    [InlineData("ghes", true)]
    public void Identifier_Matches_Plan_Ghes(string expression, bool expected)
    {
        Assert.Equal(expected, VersionExpressionEvaluator.Evaluate(expression, DocsVersion.Ghes("3.21")));
    }

    [Fact]
    public void Or_Combines_Two_Plans()
    {
        Assert.True(VersionExpressionEvaluator.Evaluate("fpt or ghec", DocsVersion.Fpt));
        Assert.True(VersionExpressionEvaluator.Evaluate("fpt or ghec", DocsVersion.Ghec));
        Assert.False(VersionExpressionEvaluator.Evaluate("fpt or ghec", DocsVersion.Ghes("3.21")));
    }

    [Fact]
    public void And_Requires_Both()
    {
        // 実用上 (fpt and ghec) は常に false だが parser の検証用。
        Assert.False(VersionExpressionEvaluator.Evaluate("fpt and ghec", DocsVersion.Fpt));
    }

    [Fact]
    public void Not_Inverts_Result()
    {
        Assert.False(VersionExpressionEvaluator.Evaluate("not fpt", DocsVersion.Fpt));
        Assert.True(VersionExpressionEvaluator.Evaluate("not fpt", DocsVersion.Ghec));
    }

    [Fact]
    public void Parentheses_Group_Subexpressions()
    {
        Assert.True(VersionExpressionEvaluator.Evaluate("(fpt or ghec) and not ghes", DocsVersion.Fpt));
        Assert.False(VersionExpressionEvaluator.Evaluate("(fpt or ghec) and not ghes", DocsVersion.Ghes("3.21")));
    }

    [Theory]
    [InlineData("ghes < 3.20", "3.18", true)]
    [InlineData("ghes < 3.20", "3.20", false)]
    [InlineData("ghes < 3.20", "3.21", false)]
    [InlineData("ghes <= 3.20", "3.20", true)]
    [InlineData("ghes > 3.20", "3.21", true)]
    [InlineData("ghes > 3.20", "3.20", false)]
    [InlineData("ghes >= 3.20", "3.20", true)]
    [InlineData("ghes = 3.21", "3.21", true)]
    [InlineData("ghes = 3.21", "3.20", false)]
    [InlineData("ghes != 3.21", "3.20", true)]
    [InlineData("ghes != 3.21", "3.21", false)]
    public void Comparison_Against_Ghes_Release(string expression, string release, bool expected)
    {
        Assert.Equal(expected, VersionExpressionEvaluator.Evaluate(expression, DocsVersion.Ghes(release)));
    }

    [Fact]
    public void Comparison_Is_False_For_Non_Ghes_Plan()
    {
        // ghes < 3.20 on fpt → ghes 比較は ghes plan のときだけ意味を持つので false。
        Assert.False(VersionExpressionEvaluator.Evaluate("ghes < 3.20", DocsVersion.Fpt));
        Assert.False(VersionExpressionEvaluator.Evaluate("ghes < 3.20", DocsVersion.Ghec));
    }

    [Fact]
    public void Combined_Expression_With_Comparison()
    {
        // fpt or ghec or (ghes >= 3.20)
        var expr = "fpt or ghec or ghes >= 3.20";
        Assert.True(VersionExpressionEvaluator.Evaluate(expr, DocsVersion.Fpt));
        Assert.True(VersionExpressionEvaluator.Evaluate(expr, DocsVersion.Ghec));
        Assert.True(VersionExpressionEvaluator.Evaluate(expr, DocsVersion.Ghes("3.20")));
        Assert.True(VersionExpressionEvaluator.Evaluate(expr, DocsVersion.Ghes("3.21")));
        Assert.False(VersionExpressionEvaluator.Evaluate(expr, DocsVersion.Ghes("3.18")));
    }

    [Fact]
    public void Unknown_Feature_Flag_Identifier_Returns_True_To_Avoid_Hiding_Content()
    {
        // {% ifversion copilot-some-feature %} のような feature 名は data/features を
        // 読んでいないので、保守的に true を返してレビュアーに本文を見せる。
        Assert.True(VersionExpressionEvaluator.Evaluate("copilot-chat", DocsVersion.Fpt));
        Assert.True(VersionExpressionEvaluator.Evaluate("copilot-chat", DocsVersion.Ghes("3.18")));
    }

    [Fact]
    public void Unknown_Feature_With_Plan_Combination()
    {
        // copilot-feature and ghec → feature は true → ghec の真偽がそのまま。
        Assert.True(VersionExpressionEvaluator.Evaluate("copilot-feature and ghec", DocsVersion.Ghec));
        Assert.False(VersionExpressionEvaluator.Evaluate("copilot-feature and ghec", DocsVersion.Fpt));
    }

    [Fact]
    public void Empty_Or_Whitespace_Expression_Returns_True()
    {
        Assert.True(VersionExpressionEvaluator.Evaluate(string.Empty, DocsVersion.Fpt));
        Assert.True(VersionExpressionEvaluator.Evaluate("   ", DocsVersion.Fpt));
    }

    [Fact]
    public void Malformed_Expression_Returns_True_As_Safe_Default()
    {
        Assert.True(VersionExpressionEvaluator.Evaluate("ghes <", DocsVersion.Ghes("3.21")));
        Assert.True(VersionExpressionEvaluator.Evaluate("((fpt", DocsVersion.Fpt));
    }
}
