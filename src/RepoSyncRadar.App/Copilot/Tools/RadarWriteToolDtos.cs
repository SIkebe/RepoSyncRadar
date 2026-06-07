using System.ComponentModel;
using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.App.Copilot.Tools;

/// <summary>Args for <c>radar_save_review</c>.</summary>
public sealed record SaveReviewArgs(string Sha, ReviewStatus Status, string? Reason);

/// <summary>Args for <c>radar_score_commit</c>.</summary>
public sealed record ScoreCommitArgs(
    [property: Description("Commit SHA to score.")]
    string Sha,
    [property: Description("Importance score from 0.0 to 1.0. Use higher values for reader-impacting docs changes.")]
    double Score,
    [property: Description("Short category label such as feature-update, breaking-change, security, deprecation, docs-maintenance, or low-signal.")]
    string Category,
    [property: Description("Audience tags such as devrel, customer, admin, developer, support, partner, or internal.")]
    IReadOnlyList<string> Audience,
    [property: Description("One-sentence Japanese summary of what changed. For GitHub product scope terms, keep Organization and Enterprise in English; do not translate either concept as 組織.")]
    string SummaryJa,
    [property: Description("Short Japanese reason for the score and triage decision. For GitHub product scope terms, keep Organization and Enterprise in English; do not translate either concept as 組織.")]
    string WhyJa,
    [property: Description("Compact Japanese analysis for UI display. Must contain exactly these labeled lines in order: 変更内容, 根拠, 影響, 確認観点. Each line should be concise, factual, avoid repeating SummaryJa/WhyJa, and keep GitHub Organization and Enterprise in English instead of translating either concept as 組織.")]
    string DetailsJa,
    [property: Description("Model identifier used by the Copilot session.")]
    string Model,
    [property: Description("Prompt/version hash used for this scoring run.")]
    string PromptHash);

/// <summary>Args for <c>radar_post_draft</c>.</summary>
public sealed record PostDraftArgs(string Sha, string Channel, string? Body);

/// <summary>Args for <c>radar_ignore_rule</c>.</summary>
public sealed record IgnoreRuleArgs(string Pattern, string? Reason);

/// <summary>Args for <c>radar_boost_rule</c>.</summary>
public sealed record BoostRuleArgs(string Pattern, double Delta, string? Reason);

/// <summary>Result envelope returned by every write tool. <see cref="Error"/> is null on success.</summary>
public sealed record WriteResult(string? Error);
