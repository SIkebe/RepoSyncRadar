using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.Core.Services;

public enum TriagePreflightGitHubEstimateStatus
{
    Skipped,
    Succeeded,
    Failed,
}

public sealed record TriagePreflightGitHubSettings(
    string Owner,
    string Repo,
    string PullRequestTitleFilter,
    int MaxPullRequests,
    DateTimeOffset? PullRequestCreatedAtOrAfter);

public sealed record TriagePreflightSummary(
    TriagePreflightGitHubSettings GitHubSettings,
    TriagePreflightGitHubEstimateStatus GitHubEstimateStatus,
    int? CandidatePullRequestCount,
    int? NewUnseenCommitCount,
    string? GitHubEstimateUnavailableReason,
    IReadOnlyDictionary<ReviewStatus, int> ReviewCounts,
    int UnscoredUnreviewedCommitCount,
    int EstimatedScoringTargetCount,
    int PerRunScoringLimit,
    int IgnoreRuleCount)
{
    public bool HasIgnoreRules => IgnoreRuleCount > 0;
}
