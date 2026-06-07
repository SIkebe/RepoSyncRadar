using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core.Services;

public sealed class TriagePreflightSummaryBuilder : ITriagePreflightSummaryBuilder
{
    public const int ScoringTargetLimit = 50;

    private const string _signInRequiredReason = "sign-in-required";

    private readonly IRadarRepository _repository;
    private readonly IDocsGitHubClient _docs;
    private readonly GitHubOptions _options;

    public TriagePreflightSummaryBuilder(
        IRadarRepository repository,
        IDocsGitHubClient docs,
        IOptions<GitHubOptions> options)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(options);

        _repository = repository;
        _docs = docs;
        _options = options.Value;
    }

    public async Task<TriagePreflightSummary> BuildAsync(
        bool includeGitHubEstimate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var reviewCounts = NormalizeReviewCounts(
            await _repository.GetReviewCountsAsync(cancellationToken).ConfigureAwait(false));
        var unscoredUnreviewed = await _repository.QueryCommitsAsync(
            new CommitQueryFilter
            {
                Status = ReviewStatus.Unseen,
                UnscoredOnly = true,
            },
            cancellationToken).ConfigureAwait(false);
        var ignoreRules = await _repository.GetIgnoreRulesAsync(cancellationToken).ConfigureAwait(false);

        var githubStatus = TriagePreflightGitHubEstimateStatus.Skipped;
        int? candidatePullRequestCount = null;
        int? newUnseenCommitCount = null;
        string? unavailableReason = _signInRequiredReason;

        if (includeGitHubEstimate)
        {
            try
            {
                var estimate = await _docs.EstimateTriageAsync(cancellationToken).ConfigureAwait(false);
                githubStatus = TriagePreflightGitHubEstimateStatus.Succeeded;
                candidatePullRequestCount = estimate.CandidatePullRequestCount;
                newUnseenCommitCount = estimate.NewUnseenCommitCount;
                unavailableReason = null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                githubStatus = TriagePreflightGitHubEstimateStatus.Failed;
                unavailableReason = ex.Message;
            }
        }

        var estimatedScoringTargets = Math.Min(
            ScoringTargetLimit,
            unscoredUnreviewed.Count + (newUnseenCommitCount ?? 0));

        return new TriagePreflightSummary(
            new TriagePreflightGitHubSettings(
                _options.Owner,
                _options.Repo,
                _options.PullRequestTitleFilter,
                _options.MaxPullRequests,
                _options.PullRequestCreatedAtOrAfter),
            githubStatus,
            candidatePullRequestCount,
            newUnseenCommitCount,
            unavailableReason,
            reviewCounts,
            unscoredUnreviewed.Count,
            estimatedScoringTargets,
            ScoringTargetLimit,
            ignoreRules.Count);
    }

    private static Dictionary<ReviewStatus, int> NormalizeReviewCounts(
        IReadOnlyDictionary<ReviewStatus, int> source)
    {
        var counts = Enum.GetValues<ReviewStatus>()
            .ToDictionary(static status => status, static _ => 0);

        foreach (var (status, count) in source)
        {
            counts[status] = count;
        }

        return counts;
    }
}
