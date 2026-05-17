using System.ComponentModel.DataAnnotations;

namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Strongly-typed options for talking to <c>github/docs</c> via Octokit.
/// </summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Repository owner, defaults to <c>github</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Owner { get; set; } = "github";

    /// <summary>Repository name, defaults to <c>docs</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Repo { get; set; } = "docs";

    /// <summary>Pull request title prefix to watch (e.g. <c>Repo sync</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string PullRequestTitleFilter { get; set; } = "Repo sync";

    /// <summary>How many title-matching PRs to collect on each fetch.</summary>
    [Range(1, 100)]
    public int MaxPullRequests { get; set; } = 5;
}
