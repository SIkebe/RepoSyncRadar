using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.App.Copilot;
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
            DetailsJa: "変更内容: 詳細\n根拠: diff",
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
        Assert.Equal("変更内容: 詳細\n根拠: diff", row.DetailsJa);
        Assert.Contains("devrel", row.AudienceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScoreCommit_AutoRejects_Low_Score_When_Unreviewed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("aaa", ct);
        var tools = harness.CreateTools();

        var result = await tools.ScoreCommitAsync(new ScoreCommitArgs(
            Sha: "aaa",
            Score: RadarWriteTools.AutoRejectScoreThreshold,
            Category: "low-signal",
            Audience: ["internal"],
            SummaryJa: "要約",
            WhyJa: "理由",
            DetailsJa: "詳細",
            Model: "gpt-test",
            PromptHash: "hash"), ct);

        Assert.Null(result.Error);
        await using var db = harness.CreateDb();
        var review = await db.Reviews.SingleAsync(r => r.Sha == "aaa", ct);
        Assert.Equal(ReviewStatus.Rejected, review.Status);
        Assert.Equal(RadarWriteTools.AutoRejectedLowScoreReason, review.Reason);
        Assert.NotNull(review.ReviewedAt);
    }

    [Fact]
    public async Task ScoreCommit_Does_Not_Override_User_Review_For_Low_Score()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync(
            "aaa",
            ReviewStatus.Adopted,
            message: "user-selected",
            cancellationToken: ct);
        var tools = harness.CreateTools();

        var result = await tools.ScoreCommitAsync(new ScoreCommitArgs(
            Sha: "aaa",
            Score: 0.1,
            Category: "low-signal",
            Audience: ["internal"],
            SummaryJa: "要約",
            WhyJa: "理由",
            DetailsJa: "詳細",
            Model: "gpt-test",
            PromptHash: "hash"), ct);

        Assert.Null(result.Error);
        await using var db = harness.CreateDb();
        var review = await db.Reviews.SingleAsync(r => r.Sha == "aaa", ct);
        Assert.Equal(ReviewStatus.Adopted, review.Status);
        Assert.Null(review.Reason);
    }

    [Fact]
    public async Task ScoreCommit_Reports_Triage_Current_Position_After_Save()
    {
        var ct = TestContext.Current.CancellationToken;
        var progress = new CapturingProgress();
        var tracker = new TriageScoringProgressTracker();
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("aaa1111111111111111111111111111111111111", ct);
        await harness.InsertCommitAsync("bbb2222222222222222222222222222222222222", ct);
        var tools = new RadarWriteTools(harness.DbFactory, tracker);

        using var scope = tracker.Begin(progress);
        tracker.ReportCommitList([
            "aaa1111111111111111111111111111111111111",
            "bbb2222222222222222222222222222222222222",
        ]);

        var result = await tools.ScoreCommitAsync(new ScoreCommitArgs(
            Sha: "aaa1111111111111111111111111111111111111",
            Score: 0.82,
            Category: "feature",
            Audience: ["devrel"],
            SummaryJa: "要約",
            WhyJa: "理由",
            DetailsJa: "変更内容: 詳細",
            Model: "gpt-test",
            PromptHash: "deadbee"), ct);

        Assert.Null(result.Error);
        Assert.Contains(progress.Messages, message => message.Contains("分析 1 / 2 件", StringComparison.Ordinal)
            && message.Contains("スコア保存 1 / 2 件", StringComparison.Ordinal)
            && message.Contains("aaa11111", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScoreCommit_Publishes_Review_Broadcast_So_Commit_List_Refreshes()
    {
        var ct = TestContext.Current.CancellationToken;
        var broadcaster = new ReviewBroadcaster();
        var publishCount = 0;
        broadcaster.Reviewed += (_, _) => Interlocked.Increment(ref publishCount);

        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("aaa", ct);
        var tools = new RadarWriteTools(harness.DbFactory, new TriageScoringProgressTracker(), broadcaster);

        var result = await tools.ScoreCommitAsync(new ScoreCommitArgs(
            Sha: "aaa",
            Score: 0.5,
            Category: "docs-maintenance",
            Audience: ["developer"],
            SummaryJa: "要約",
            WhyJa: "理由",
            DetailsJa: "詳細",
            Model: "gpt-test",
            PromptHash: "hash"), ct);

        Assert.Null(result.Error);
        Assert.Equal(1, publishCount);
    }

    [Fact]
    public async Task SaveReview_Publishes_Review_Broadcast()
    {
        var ct = TestContext.Current.CancellationToken;
        var broadcaster = new ReviewBroadcaster();
        var publishCount = 0;
        broadcaster.Reviewed += (_, _) => Interlocked.Increment(ref publishCount);

        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("aaa", ct);
        var tools = new RadarWriteTools(harness.DbFactory, new TriageScoringProgressTracker(), broadcaster);

        var result = await tools.SaveReviewAsync(new SaveReviewArgs("aaa", ReviewStatus.Rejected, "noise"), ct);

        Assert.Null(result.Error);
        Assert.Equal(1, publishCount);
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
    public async Task PostDraft_Rejects_Unsupported_Channel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("aaa", ct);
        var tools = harness.CreateTools();

        var result = await tools.PostDraftAsync(new PostDraftArgs("aaa", "teams", Body: "body"), ct);

        Assert.NotNull(result.Error);
        Assert.Contains("Unsupported draft channel", result.Error, StringComparison.Ordinal);
        await using var db = harness.CreateDb();
        Assert.Equal(0, await db.Drafts.CountAsync(ct));
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
    public async Task BoostRule_Upserts_And_Preserves_CreatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        var tools = harness.CreateTools();

        var first = await tools.BoostRuleAsync(new BoostRuleArgs("content/**", Delta: 2.5, Reason: "important"), ct);
        await using (var db = harness.CreateDb())
        {
            var inserted = await db.BoostRules.SingleAsync(ct);
            Assert.Null(first.Error);
            Assert.Equal(2.5, inserted.Delta);
            Assert.Equal("important", inserted.Reason);
        }

        DateTime createdAt;
        await using (var db = harness.CreateDb())
        {
            createdAt = await db.BoostRules
                .Where(rule => rule.Pattern == "content/**")
                .Select(rule => rule.CreatedAt)
                .SingleAsync(ct);
        }

        var second = await tools.BoostRuleAsync(new BoostRuleArgs("content/**", Delta: -1.25, Reason: "lower"), ct);

        Assert.Null(second.Error);
        await using (var db = harness.CreateDb())
        {
            var updated = await db.BoostRules.SingleAsync(ct);
            Assert.Equal("content/**", updated.Pattern);
            Assert.Equal(-1.25, updated.Delta);
            Assert.Equal("lower", updated.Reason);
            Assert.Equal(createdAt, updated.CreatedAt);
        }
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

    [Fact]
    public void ScoreCommit_Metadata_Includes_GitHub_Scope_Terminology_Rule()
    {
        var description = string.Join(
            "\n",
            GetDescription(nameof(ScoreCommitArgs.SummaryJa)),
            GetDescription(nameof(ScoreCommitArgs.WhyJa)),
            GetDescription(nameof(ScoreCommitArgs.DetailsJa)));

        Assert.Contains("Organization", description, StringComparison.Ordinal);
        Assert.Contains("Enterprise", description, StringComparison.Ordinal);
        Assert.Contains("組織", description, StringComparison.Ordinal);
    }

    private static string GetDescription(string propertyName)
    {
        var property = typeof(ScoreCommitArgs).GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property not found: {propertyName}.");
        return property
            .GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
            .OfType<DescriptionAttribute>()
            .Single()
            .Description;
    }

    private sealed class CapturingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }
}
