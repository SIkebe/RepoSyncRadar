namespace RepoSyncRadar.Core.Models;

/// <summary>
/// A single commit harvested from a <c>Repo sync</c> PR in <c>github/docs</c>.
/// </summary>
public sealed class Commit
{
    public string Sha { get; set; } = default!;

    public int PrNumber { get; set; }

    public string Message { get; set; } = default!;

    public string Author { get; set; } = default!;

    public DateTime AuthoredAt { get; set; }

    public DateTime FetchedAt { get; set; }

    public List<CommitFile> Files { get; set; } = new();

    public Scoring? Scoring { get; set; }

    public Review? Review { get; set; }

    public List<Draft> Drafts { get; set; } = new();

    public List<ReviewHistory> ReviewHistory { get; set; } = new();
}

/// <summary>
/// A file changed by a commit, with line-level deltas.
/// </summary>
public sealed class CommitFile
{
    public string Sha { get; set; } = default!;

    public string Path { get; set; } = default!;

    /// <summary>One of <c>added</c>, <c>modified</c>, <c>removed</c>, <c>renamed</c>.</summary>
    public string Status { get; set; } = default!;

    public int Additions { get; set; }

    public int Deletions { get; set; }
}

/// <summary>
/// LLM-produced scoring and summary for a commit. Populated by the Morning Triage session.
/// </summary>
public sealed class Scoring
{
    public string Sha { get; set; } = default!;

    public double Score { get; set; }

    public string Category { get; set; } = default!;

    /// <summary>JSON-serialized array of audience tags (e.g. <c>["devrel","customer"]</c>).</summary>
    public string AudienceJson { get; set; } = "[]";

    public string SummaryJa { get; set; } = string.Empty;

    public string WhyJa { get; set; } = string.Empty;

    public string DetailsJa { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string PromptHash { get; set; } = string.Empty;

    public DateTime ScoredAt { get; set; }
}

/// <summary>
/// Review grouping status for a commit. Drives sidebar grouping and learning signals.
/// </summary>
public enum ReviewStatus
{
    Unseen,
    Seen,
    Adopted,
    Rejected,
    Archived,
    Later,
}

public sealed class Review
{
    public string Sha { get; set; } = default!;

    public ReviewStatus Status { get; set; } = ReviewStatus.Unseen;

    /// <summary>Free-text reason captured when the user focuses or manually archives a commit. Used as few-shot.</summary>
    public string? Reason { get; set; }

    public DateTime? ReviewedAt { get; set; }
}

public sealed class ReviewHistory
{
    public int Id { get; set; }

    public string Sha { get; set; } = default!;

    public ReviewStatus Status { get; set; } = ReviewStatus.Unseen;

    public string? Reason { get; set; }

    public DateTime ChangedAt { get; set; }

    public string Source { get; set; } = ReviewHistorySources.User;
}

public static class ReviewHistorySources
{
    public const string User = "user";
    public const string AutoIgnore = "auto-ignore";
    public const string BulkIgnore = "bulk-ignore";
}

/// <summary>
/// A media-specific draft generated for a focused commit.
/// </summary>
public sealed class Draft
{
    public int Id { get; set; }

    public string Sha { get; set; } = default!;

    /// <summary>One of <c>explanation</c>, <c>twitter</c>, <c>customer</c>. Legacy databases may contain <c>teams</c>.</summary>
    public string Channel { get; set; } = default!;

    public string Body { get; set; } = default!;

    public bool Posted { get; set; }

    public string? PostedUrl { get; set; }

    public DateTime GeneratedAt { get; set; }
}

/// <summary>
/// Cache of repository-relative path to canonical <c>docs.github.com</c> URLs.
/// </summary>
public sealed class PathUrlMap
{
    public string Path { get; set; } = default!;

    public string Version { get; set; } = default!;

    public string Language { get; set; } = default!;

    public string Url { get; set; } = default!;

    public DateTime ResolvedAt { get; set; }
}

/// <summary>
/// Glob-pattern rules that auto-mark matching commits as low-priority.
/// </summary>
public sealed class IgnoreRule
{
    public string Pattern { get; set; } = default!;

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Glob-pattern rules that bump or lower a commit's score.
/// </summary>
public sealed class BoostRule
{
    public string Pattern { get; set; } = default!;

    public double Delta { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// Audit trail of every Copilot tool invocation (from <c>OnPreToolUse</c> / <c>OnPostToolUse</c> hooks).
/// </summary>
public sealed class CopilotToolLog
{
    public int Id { get; set; }

    public string SessionId { get; set; } = default!;

    public string ToolName { get; set; } = default!;

    public string ArgsJson { get; set; } = string.Empty;

    public string ResultJson { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime EndedAt { get; set; }
}

public sealed record CommitHistorySnapshot(
    Commit? Commit,
    IReadOnlyList<ReviewHistory> ReviewHistory,
    IReadOnlyList<Draft> Drafts,
    IReadOnlyList<IgnoreRule> IgnoreRules);
