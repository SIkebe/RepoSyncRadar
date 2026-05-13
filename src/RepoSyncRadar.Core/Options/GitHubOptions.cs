namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Strongly-typed options for talking to <c>github/docs</c> via Octokit.
/// </summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Repository owner, defaults to <c>github</c>.</summary>
    public string Owner { get; set; } = "github";

    /// <summary>Repository name, defaults to <c>docs</c>.</summary>
    public string Repo { get; set; } = "docs";

    /// <summary>Pull request title prefix to watch (e.g. <c>Repo sync</c>).</summary>
    public string PullRequestTitleFilter { get; set; } = "Repo sync";

    /// <summary>Optional. When empty, the app uses Windows Credential Manager (DPAPI).</summary>
    public string? PersonalAccessToken { get; set; }

    /// <summary>How many PRs to scan on each fetch.</summary>
    public int MaxPullRequests { get; set; } = 5;
}
