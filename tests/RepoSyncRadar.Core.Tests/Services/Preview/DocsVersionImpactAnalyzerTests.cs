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
}
