using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.Core.Data;

/// <summary>
/// EF Core-backed implementation of <see cref="IRadarRepository"/>. Each call materializes a
/// short-lived <see cref="RadarDbContext"/> from <see cref="IDbContextFactory{TContext}"/>;
/// the host registers the repository as a singleton because the factory is the unit of state.
/// </summary>
public sealed class RadarRepository : IRadarRepository
{
    private readonly IDbContextFactory<RadarDbContext> _contextFactory;

    public RadarRepository(IDbContextFactory<RadarDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlySet<string>> GetKnownShasAsync(CancellationToken cancellationToken = default)
    {
        using var db = _contextFactory.CreateDbContext();
        var shas = await db.Commits
            .AsNoTracking()
            .Select(c => c.Sha)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new HashSet<string>(shas, StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> GetKnownShasAsync(
        IEnumerable<string> candidateShas,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateShas);

        var candidates = candidateShas
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        using var db = _contextFactory.CreateDbContext();
        var matches = await db.Commits
            .AsNoTracking()
            .Where(c => candidates.Contains(c.Sha))
            .Select(c => c.Sha)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HashSet<string>(matches, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> UpsertCommitsAsync(
        IEnumerable<Commit> commits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commits);

        // Materialize and dedup the input so repeated SHAs in the same batch are inserted once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<Commit>();
        foreach (var commit in commits)
        {
            if (commit is null || string.IsNullOrEmpty(commit.Sha))
            {
                continue;
            }
            if (seen.Add(commit.Sha))
            {
                batch.Add(commit);
            }
        }

        if (batch.Count == 0)
        {
            return Array.Empty<string>();
        }

        using var db = _contextFactory.CreateDbContext();
        var batchShas = batch.Select(c => c.Sha).ToList();
        var existing = await db.Commits
            .AsNoTracking()
            .Where(c => batchShas.Contains(c.Sha))
            .Select(c => c.Sha)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);

        var inserted = new List<string>(batch.Count);
        foreach (var commit in batch)
        {
            if (existingSet.Contains(commit.Sha))
            {
                continue;
            }
            db.Commits.Add(commit);
            inserted.Add(commit.Sha);
        }

        if (inserted.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return inserted;
    }

    public async Task SetReviewAsync(
        string sha,
        ReviewStatus status,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha);

        using var db = _contextFactory.CreateDbContext();
        var review = await db.Reviews
            .FirstOrDefaultAsync(r => r.Sha == sha, cancellationToken)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        if (review is null)
        {
            db.Reviews.Add(new Review
            {
                Sha = sha,
                Status = status,
                Reason = reason,
                ReviewedAt = now,
            });
        }
        else
        {
            review.Status = status;
            review.Reason = reason;
            review.ReviewedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Commit>> QueryCommitsAsync(
        CommitQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var db = _contextFactory.CreateDbContext();
        IQueryable<Commit> query = db.Commits
            .AsNoTracking()
            .Include(c => c.Files)
            .Include(c => c.Review)
            .Include(c => c.Scoring);

        if (filter.Status is { } status)
        {
            query = status == ReviewStatus.Unseen
                ? query.Where(c => c.Review == null
                    || c.Review.Status == ReviewStatus.Unseen
                    || c.Review.Status == ReviewStatus.Seen)
                : query.Where(c => c.Review != null && c.Review.Status == status);
        }

        query = query.OrderByDescending(c => c.AuthoredAt);

        if (filter.Limit is { } limit && limit >= 0)
        {
            query = query.Take(limit);
        }

        var commits = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return commits;
    }

    public async Task<IReadOnlyDictionary<ReviewStatus, int>> GetReviewCountsAsync(
        CancellationToken cancellationToken = default)
    {
        using var db = _contextFactory.CreateDbContext();

        // Commits with no Review row count toward Unseen.
        var unseenFromMissing = await db.Commits
            .AsNoTracking()
            .CountAsync(c => c.Review == null, cancellationToken)
            .ConfigureAwait(false);

        var grouped = await db.Reviews
            .AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var counts = new Dictionary<ReviewStatus, int>
        {
            [ReviewStatus.Unseen] = unseenFromMissing,
            [ReviewStatus.Seen] = 0,
            [ReviewStatus.Adopted] = 0,
            [ReviewStatus.Rejected] = 0,
            [ReviewStatus.Later] = 0,
        };

        foreach (var entry in grouped)
        {
            if (entry.Key == ReviewStatus.Seen)
            {
                counts[ReviewStatus.Unseen] = counts[ReviewStatus.Unseen] + entry.Count;
                continue;
            }

            counts[entry.Key] = counts[entry.Key] + entry.Count;
        }

        return counts;
    }

    public async Task<bool> AddIgnoreRuleAsync(
        string pattern,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        using var db = _contextFactory.CreateDbContext();
        var exists = await db.IgnoreRules
            .AsNoTracking()
            .AnyAsync(r => r.Pattern == pattern, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            return false;
        }

        db.IgnoreRules.Add(new IgnoreRule
        {
            Pattern = pattern,
            Reason = reason,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<IgnoreRule>> GetIgnoreRulesAsync(
        CancellationToken cancellationToken = default)
    {
        using var db = _contextFactory.CreateDbContext();
        return await db.IgnoreRules
            .AsNoTracking()
            .OrderByDescending(rule => rule.CreatedAt)
            .ThenBy(rule => rule.Pattern)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> BulkRejectByPathPrefixAsync(
        string pathPrefix,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);
        ArgumentNullException.ThrowIfNull(reason);

        using var db = _contextFactory.CreateDbContext();
        var likePattern = EscapeLike(pathPrefix) + "%";
        var matchingShas = await db.Commits
            .Where(c => (c.Review == null || c.Review.Status == ReviewStatus.Unseen)
                && c.Files.Any(f => EF.Functions.Like(f.Path, likePattern)))
            .Select(c => c.Sha)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (matchingShas.Count == 0)
        {
            return 0;
        }

        var existingReviews = await db.Reviews
            .Where(r => matchingShas.Contains(r.Sha))
            .ToDictionaryAsync(r => r.Sha, cancellationToken)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        foreach (var sha in matchingShas)
        {
            if (existingReviews.TryGetValue(sha, out var review))
            {
                review.Status = ReviewStatus.Rejected;
                review.Reason = reason;
                review.ReviewedAt = now;
            }
            else
            {
                db.Reviews.Add(new Review
                {
                    Sha = sha,
                    Status = ReviewStatus.Rejected,
                    Reason = reason,
                    ReviewedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return matchingShas.Count;
    }

    /// <summary>Escapes the LIKE wildcards (<c>%</c> and <c>_</c>) inside a literal path prefix.</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
