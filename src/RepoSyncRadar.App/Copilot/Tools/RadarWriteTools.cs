using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.App.Copilot.Tools;

/// <summary>
/// Registers the side-effecting <c>radar_*</c> Copilot tools (Step 14). All tools here
/// have <c>skip_permission</c> intentionally NOT set, so the SDK routes them through
/// <see cref="RadarPermissionPolicy"/>. Morning Triage scoring/review writes are
/// pre-approved there; stronger side effects such as draft/rule writes still prompt.
/// </summary>
public sealed class RadarWriteTools
{
    private const double BoostDeltaMax = 5.0;
    private const double BoostDeltaMin = -5.0;

    private readonly IDbContextFactory<RadarDbContext> _dbFactory;

    public RadarWriteTools(IDbContextFactory<RadarDbContext> dbFactory)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        _dbFactory = dbFactory;
    }

    /// <summary>Returns the five write tools as <see cref="AIFunction"/> instances.</summary>
    public IReadOnlyList<AIFunction> CreateAll()
    {
        return [
            CreateScoreCommit(),
            CreateSaveReview(),
            CreatePostDraft(),
            CreateIgnoreRule(),
            CreateBoostRule(),
        ];
    }

    internal async Task<WriteResult> SaveReviewAsync(SaveReviewArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(args.Sha))
        {
            return new WriteResult("sha is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var commitExists = await db.Commits.AnyAsync(c => c.Sha == args.Sha, cancellationToken).ConfigureAwait(false);
        if (!commitExists)
        {
            return new WriteResult($"Unknown commit sha: {args.Sha}.");
        }

        var existing = await db.Reviews.FirstOrDefaultAsync(r => r.Sha == args.Sha, cancellationToken).ConfigureAwait(false);
        var nowUtc = DateTime.UtcNow;
        if (existing is null)
        {
            db.Reviews.Add(new Review
            {
                Sha = args.Sha,
                Status = args.Status,
                Reason = args.Reason,
                ReviewedAt = nowUtc,
            });
        }
        else
        {
            existing.Status = args.Status;
            existing.Reason = args.Reason;
            existing.ReviewedAt = nowUtc;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new WriteResult(Error: null);
    }

    internal async Task<WriteResult> ScoreCommitAsync(ScoreCommitArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(args.Sha))
        {
            return new WriteResult("sha is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var commitExists = await db.Commits.AnyAsync(c => c.Sha == args.Sha, cancellationToken).ConfigureAwait(false);
        if (!commitExists)
        {
            return new WriteResult($"Unknown commit sha: {args.Sha}.");
        }

        var existing = await db.Scorings.FirstOrDefaultAsync(s => s.Sha == args.Sha, cancellationToken).ConfigureAwait(false);
        var audienceJson = JsonSerializer.Serialize(args.Audience ?? []);
        var nowUtc = DateTime.UtcNow;
        if (existing is null)
        {
            db.Scorings.Add(new Scoring
            {
                Sha = args.Sha,
                Score = args.Score,
                Category = args.Category ?? string.Empty,
                AudienceJson = audienceJson,
                SummaryJa = args.SummaryJa ?? string.Empty,
                WhyJa = args.WhyJa ?? string.Empty,
                Model = args.Model ?? string.Empty,
                PromptHash = args.PromptHash ?? string.Empty,
                ScoredAt = nowUtc,
            });
        }
        else
        {
            existing.Score = args.Score;
            existing.Category = args.Category ?? string.Empty;
            existing.AudienceJson = audienceJson;
            existing.SummaryJa = args.SummaryJa ?? string.Empty;
            existing.WhyJa = args.WhyJa ?? string.Empty;
            existing.Model = args.Model ?? string.Empty;
            existing.PromptHash = args.PromptHash ?? string.Empty;
            existing.ScoredAt = nowUtc;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new WriteResult(Error: null);
    }

    internal async Task<WriteResult> PostDraftAsync(PostDraftArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(args.Sha))
        {
            return new WriteResult("sha is required.");
        }
        if (string.IsNullOrWhiteSpace(args.Channel))
        {
            return new WriteResult("channel is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var commitExists = await db.Commits.AnyAsync(c => c.Sha == args.Sha, cancellationToken).ConfigureAwait(false);
        if (!commitExists)
        {
            return new WriteResult($"Unknown commit sha: {args.Sha}.");
        }

        db.Drafts.Add(new Draft
        {
            Sha = args.Sha,
            Channel = args.Channel,
            Body = args.Body ?? string.Empty,
            Posted = false,
            GeneratedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new WriteResult(Error: null);
    }

    internal async Task<WriteResult> IgnoreRuleAsync(IgnoreRuleArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(args.Pattern))
        {
            return new WriteResult("pattern is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var exists = await db.IgnoreRules.AnyAsync(r => r.Pattern == args.Pattern, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return new WriteResult($"Ignore rule already exists for pattern: {args.Pattern}.");
        }

        db.IgnoreRules.Add(new IgnoreRule
        {
            Pattern = args.Pattern,
            Reason = args.Reason,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new WriteResult(Error: null);
    }

    internal async Task<WriteResult> BoostRuleAsync(BoostRuleArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(args.Pattern))
        {
            return new WriteResult("pattern is required.");
        }
        if (double.IsNaN(args.Delta) || args.Delta > BoostDeltaMax || args.Delta < BoostDeltaMin)
        {
            return new WriteResult($"delta must be between {BoostDeltaMin} and {BoostDeltaMax}.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.BoostRules.FirstOrDefaultAsync(r => r.Pattern == args.Pattern, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.BoostRules.Add(new BoostRule
            {
                Pattern = args.Pattern,
                Delta = args.Delta,
                Reason = args.Reason,
            });
        }
        else
        {
            existing.Delta = args.Delta;
            existing.Reason = args.Reason;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new WriteResult(Error: null);
    }

    private AIFunction CreateSaveReview()
    {
        return AIFunctionFactory.Create(
            ([Description("Side-effecting: writes to radar.db. Args carry sha + Adopted/Rejected/Later + optional reason.")] SaveReviewArgs args,
             CancellationToken cancellationToken)
                => SaveReviewAsync(args, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "radar_save_review",
                Description = "Persists a review verdict (Adopted/Rejected/Later) for a commit. Side-effecting.",
            });
    }

    private AIFunction CreateScoreCommit()
    {
        return AIFunctionFactory.Create(
            ([Description("Side-effecting: writes Scoring row to radar.db.")] ScoreCommitArgs args,
             CancellationToken cancellationToken)
                => ScoreCommitAsync(args, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "radar_score_commit",
                Description = "Stores or updates the LLM-produced score, category, audience tags, and summary for a commit. Side-effecting.",
            });
    }

    private AIFunction CreatePostDraft()
    {
        return AIFunctionFactory.Create(
            ([Description("Side-effecting: inserts a Draft row to radar.db. Posted=false until the user shares it.")] PostDraftArgs args,
             CancellationToken cancellationToken)
                => PostDraftAsync(args, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "radar_post_draft",
                Description = "Stores a media-specific draft (twitter / teams / customer) for a commit. Side-effecting.",
            });
    }

    private AIFunction CreateIgnoreRule()
    {
        return AIFunctionFactory.Create(
            ([Description("Side-effecting: inserts a glob-based IgnoreRule to radar.db.")] IgnoreRuleArgs args,
             CancellationToken cancellationToken)
                => IgnoreRuleAsync(args, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "radar_ignore_rule",
                Description = "Adds a glob-based rule that auto-archives matching commits. Side-effecting.",
            });
    }

    private AIFunction CreateBoostRule()
    {
        return AIFunctionFactory.Create(
            ([Description("Side-effecting: inserts/updates a BoostRule (Delta in -5..+5).")] BoostRuleArgs args,
             CancellationToken cancellationToken)
                => BoostRuleAsync(args, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "radar_boost_rule",
                Description = "Adds or updates a glob-based score-adjustment rule. Delta must be between -5 and 5. Side-effecting.",
            });
    }
}
