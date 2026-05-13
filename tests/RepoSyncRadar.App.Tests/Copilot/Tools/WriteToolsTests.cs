using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.App.Copilot.Tools;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot.Tools;

/// <summary>
/// Validates the side-effecting <c>radar_*</c> Copilot tools registered under
/// <see cref="RadarWriteTools"/>. All tests run against a temp-file SQLite db
/// migrated with the production schema.
/// </summary>
public sealed class WriteToolsTests
{
    [Fact]
    public async Task SaveReview_Persists_To_Db()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("aaa", ct);
        var tools = harness.CreateTools();

        var result = await tools.SaveReviewAsync(new SaveReviewArgs("aaa", ReviewStatus.Adopted, "good"), ct);

        Assert.Null(result.Error);
        await using var db = harness.CreateDb();
        var row = await db.Reviews.SingleAsync(r => r.Sha == "aaa", ct);
        Assert.Equal(ReviewStatus.Adopted, row.Status);
        Assert.Equal("good", row.Reason);
    }

    [Fact]
    public async Task SaveReview_Updates_Existing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("aaa", ct);
        var tools = harness.CreateTools();

        await tools.SaveReviewAsync(new SaveReviewArgs("aaa", ReviewStatus.Later, "wait"), ct);
        await tools.SaveReviewAsync(new SaveReviewArgs("aaa", ReviewStatus.Adopted, "go"), ct);

        await using var db = harness.CreateDb();
        var rows = await db.Reviews.Where(r => r.Sha == "aaa").ToListAsync(ct);
        var row = Assert.Single(rows);
        Assert.Equal(ReviewStatus.Adopted, row.Status);
        Assert.Equal("go", row.Reason);
    }

    [Fact]
    public async Task SaveReview_Rejects_Unknown_Sha()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        var tools = harness.CreateTools();

        var result = await tools.SaveReviewAsync(new SaveReviewArgs("zzz", ReviewStatus.Adopted, null), ct);

        Assert.NotNull(result.Error);
        Assert.Contains("zzz", result.Error, StringComparison.Ordinal);
        await using var db = harness.CreateDb();
        Assert.Equal(0, await db.Reviews.CountAsync(ct));
    }

    [Fact]
    public async Task ScoreCommit_Stores_PromptHash_And_Model()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("aaa", ct);
        var tools = harness.CreateTools();

        var args = new ScoreCommitArgs(
            Sha: "aaa",
            Score: 0.82,
            Category: "feature",
            Audience: ["devrel", "customer"],
            SummaryJa: "要約",
            WhyJa: "理由",
            Model: "gpt-test",
            PromptHash: "deadbee");

        var result = await tools.ScoreCommitAsync(args, ct);

        Assert.Null(result.Error);
        await using var db = harness.CreateDb();
        var row = await db.Scorings.SingleAsync(s => s.Sha == "aaa", ct);
        Assert.Equal("gpt-test", row.Model);
        Assert.Equal("deadbee", row.PromptHash);
        Assert.Equal(0.82, row.Score, precision: 4);
        Assert.Equal("feature", row.Category);
        Assert.Contains("devrel", row.AudienceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostDraft_Allows_Empty_Body_Optional()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("aaa", ct);
        var tools = harness.CreateTools();

        var result = await tools.PostDraftAsync(new PostDraftArgs("aaa", "twitter", Body: null), ct);

        Assert.Null(result.Error);
        await using var db = harness.CreateDb();
        var row = await db.Drafts.SingleAsync(d => d.Sha == "aaa", ct);
        Assert.Equal("twitter", row.Channel);
        Assert.Equal(string.Empty, row.Body);
        Assert.False(row.Posted);
    }

    [Fact]
    public async Task IgnoreRule_Duplicate_Pattern_Returns_Error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        var tools = harness.CreateTools();

        var first = await tools.IgnoreRuleAsync(new IgnoreRuleArgs("content/release-notes/**", "noisy"), ct);
        var second = await tools.IgnoreRuleAsync(new IgnoreRuleArgs("content/release-notes/**", "noisy"), ct);

        Assert.Null(first.Error);
        Assert.NotNull(second.Error);
        await using var db = harness.CreateDb();
        Assert.Equal(1, await db.IgnoreRules.CountAsync(ct));
    }

    [Fact]
    public async Task BoostRule_Out_Of_Range_Delta()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        var tools = harness.CreateTools();

        var tooHigh = await tools.BoostRuleAsync(new BoostRuleArgs("content/**", Delta: 5.5, Reason: null), ct);
        var tooLow = await tools.BoostRuleAsync(new BoostRuleArgs("content/**", Delta: -5.5, Reason: null), ct);

        Assert.NotNull(tooHigh.Error);
        Assert.NotNull(tooLow.Error);
        await using var db = harness.CreateDb();
        Assert.Equal(0, await db.BoostRules.CountAsync(ct));
    }

    [Fact]
    public void CreateAll_Registers_Five_Write_Tools_Without_SkipPermission()
    {
        var harness = WriteHarness.CreateLazy();
        var tools = harness.CreateTools();
        var functions = tools.CreateAll();

        var names = functions.Select(f => f.Name).ToHashSet();
        Assert.Equal(5, functions.Count);
        Assert.Contains("radar_score_commit", names);
        Assert.Contains("radar_save_review", names);
        Assert.Contains("radar_post_draft", names);
        Assert.Contains("radar_ignore_rule", names);
        Assert.Contains("radar_boost_rule", names);
        // Write tools must NOT opt out of permission gating.
        Assert.All(functions, f =>
        {
            if (f.AdditionalProperties.TryGetValue("skip_permission", out var value))
            {
                Assert.NotEqual(true, value);
            }
        });
    }
}
