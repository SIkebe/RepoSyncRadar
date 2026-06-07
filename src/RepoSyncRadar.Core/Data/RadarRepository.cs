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
    private static readonly string[] DisplayedDraftChannels = ["explanation", "twitter", "customer"];

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
        var ignoreRules = await db.IgnoreRules
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var inserted = new List<string>(batch.Count);
        var now = DateTime.UtcNow;
        foreach (var commit in batch)
        {
            if (existingSet.Contains(commit.Sha))
            {
                continue;
            }
            db.Commits.Add(commit);
            inserted.Add(commit.Sha);
            if (MatchesIgnoreRule(commit, ignoreRules))
            {
                var reviewedAt = now;
                db.Reviews.Add(new Review
                {
                    Sha = commit.Sha,
                    Status = ReviewStatus.Rejected,
                    Reason = "auto-ignored",
                    ReviewedAt = reviewedAt,
                });
                db.ReviewHistories.Add(new ReviewHistory
                {
                    Sha = commit.Sha,
                    Status = ReviewStatus.Rejected,
                    Reason = "auto-ignored",
                    ChangedAt = reviewedAt,
                    Source = ReviewHistorySources.AutoIgnore,
                });
            }
        }

        if (inserted.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return inserted;
    }

    private static bool MatchesIgnoreRule(Commit commit, List<IgnoreRule> ignoreRules)
    {
        if (ignoreRules.Count == 0 || commit.Files.Count == 0)
        {
            return false;
        }

        return ignoreRules.Any(rule => commit.Files.Any(file => MatchesIgnorePattern(file.Path, rule.Pattern)));
    }

    private static bool MatchesIgnorePattern(string path, string pattern)
    {
        var normalizedPath = NormalizePath(path);
        var prefix = NormalizePath(pattern).TrimEnd('*', '/');
        return prefix.Length > 0
            && (string.Equals(normalizedPath, prefix, StringComparison.Ordinal)
                || normalizedPath.StartsWith(prefix + "/", StringComparison.Ordinal));
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim().Trim('/');

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
        await AppendReviewHistoryIfChangedAsync(
                db,
                sha,
                status,
                reason,
                now,
                ReviewHistorySources.User,
                cancellationToken)
            .ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommitHistorySnapshot?> GetCommitHistoryAsync(
        string sha,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha);

        using var db = _contextFactory.CreateDbContext();
        var commit = await db.Commits
            .AsNoTracking()
            .Include(c => c.Files)
            .Include(c => c.Review)
            .Include(c => c.Scoring)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Sha == sha, cancellationToken)
            .ConfigureAwait(false);
        if (commit is null)
        {
            return null;
        }

        var history = await db.ReviewHistories
            .AsNoTracking()
            .Where(h => h.Sha == sha)
            .OrderBy(h => h.ChangedAt)
            .ThenBy(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var drafts = await db.Drafts
            .AsNoTracking()
            .Where(d => d.Sha == sha && DisplayedDraftChannels.Contains(d.Channel))
            .OrderBy(d => d.GeneratedAt)
            .ThenBy(d => d.Channel)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var ignoreRules = await db.IgnoreRules
            .AsNoTracking()
            .OrderByDescending(rule => rule.CreatedAt)
            .ThenBy(rule => rule.Pattern)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CommitHistorySnapshot(commit, history, drafts, ignoreRules);
    }

    public async Task<int> DeleteUnseenCommitsAsync(
        IEnumerable<string> shas,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shas);

        var candidates = shas
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        using var db = _contextFactory.CreateDbContext();
        var commits = await db.Commits
            .Include(c => c.Review)
            .Where(c => candidates.Contains(c.Sha))
            .Where(c => c.Review == null
                || c.Review.Status == ReviewStatus.Unseen
                || c.Review.Status == ReviewStatus.Seen)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (commits.Count == 0)
        {
            return 0;
        }

        db.Commits.RemoveRange(commits);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return commits.Count;
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

        if (filter.UnscoredOnly)
        {
            query = query.Where(c => c.Scoring == null);
        }

        var searchQuery = NormalizeSearchQuery(filter.ShaQuery);
        if (searchQuery.Length > 0)
        {
            var searchPattern = $"%{searchQuery}%";
            var prQuery = searchQuery.TrimStart('#');
            var hasPrNumber = int.TryParse(
                prQuery,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var prNumber);

            query = hasPrNumber
                ? query.Where(c => EF.Functions.Like(c.Sha, searchPattern)
                    || EF.Functions.Like(c.Message, searchPattern)
                    || c.PrNumber == prNumber)
                : query.Where(c => EF.Functions.Like(c.Sha, searchPattern)
                    || EF.Functions.Like(c.Message, searchPattern));
        }

        query = query.OrderByDescending(c => c.AuthoredAt);

        if (filter.Limit is { } limit && limit >= 0)
        {
            query = query.Take(limit);
        }

        var commits = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return commits;
    }

    private static string NormalizeSearchQuery(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

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
            [ReviewStatus.Archived] = 0,
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

    public async Task<int> DeleteIgnoreRulesAsync(
        IEnumerable<string> patterns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var candidates = patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(static pattern => pattern.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        using var db = _contextFactory.CreateDbContext();
        var rules = await db.IgnoreRules
            .Where(rule => candidates.Contains(rule.Pattern))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rules.Count == 0)
        {
            return 0;
        }

        db.IgnoreRules.RemoveRange(rules);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rules.Count;
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
            var reasonChanged = true;
            if (existingReviews.TryGetValue(sha, out var review))
            {
                reasonChanged = review.Status != ReviewStatus.Rejected
                    || !string.Equals(review.Reason, reason, StringComparison.Ordinal);
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
            if (reasonChanged)
            {
                await AppendReviewHistoryIfChangedAsync(
                        db,
                        sha,
                        ReviewStatus.Rejected,
                        reason,
                        now,
                        ReviewHistorySources.BulkIgnore,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return matchingShas.Count;
    }

    private static async Task AppendReviewHistoryIfChangedAsync(
        RadarDbContext db,
        string sha,
        ReviewStatus status,
        string? reason,
        DateTime changedAt,
        string source,
        CancellationToken cancellationToken)
    {
        var latest = await db.ReviewHistories
            .Where(h => h.Sha == sha)
            .OrderByDescending(h => h.ChangedAt)
            .ThenByDescending(h => h.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is not null
            && latest.Status == status
            && string.Equals(latest.Reason, reason, StringComparison.Ordinal))
        {
            return;
        }

        db.ReviewHistories.Add(new ReviewHistory
        {
            Sha = sha,
            Status = status,
            Reason = reason,
            ChangedAt = changedAt,
            Source = source,
        });
    }

    /// <summary>Escapes the LIKE wildcards (<c>%</c> and <c>_</c>) inside a literal path prefix.</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
