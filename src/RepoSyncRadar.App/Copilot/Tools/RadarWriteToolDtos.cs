using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.App.Copilot.Tools;

/// <summary>Args for <c>radar_save_review</c>.</summary>
public sealed record SaveReviewArgs(string Sha, ReviewStatus Status, string? Reason);

/// <summary>Args for <c>radar_score_commit</c>.</summary>
public sealed record ScoreCommitArgs(
    string Sha,
    double Score,
    string Category,
    IReadOnlyList<string> Audience,
    string SummaryJa,
    string WhyJa,
    string Model,
    string PromptHash);

/// <summary>Args for <c>radar_post_draft</c>.</summary>
public sealed record PostDraftArgs(string Sha, string Channel, string? Body);

/// <summary>Args for <c>radar_ignore_rule</c>.</summary>
public sealed record IgnoreRuleArgs(string Pattern, string? Reason);

/// <summary>Args for <c>radar_boost_rule</c>.</summary>
public sealed record BoostRuleArgs(string Pattern, double Delta, string? Reason);

/// <summary>Result envelope returned by every write tool. <see cref="Error"/> is null on success.</summary>
public sealed record WriteResult(string? Error);
