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

    [Theory]
    [InlineData("`a  b`", "`a b`")]
    [InlineData("```\na  b\n```", "```\na b\n```")]
    public void Preserves_Whitespace_Differences_Inside_Code(string before, string after)
    {
        var affected = DocsVersionImpactAnalyzer.Analyze(
            before,
            DocsLiquidContext.Empty,
            after,
            DocsLiquidContext.Empty);

        Assert.Equal(DocsVersionCatalog.All.Count, affected.Count);
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
