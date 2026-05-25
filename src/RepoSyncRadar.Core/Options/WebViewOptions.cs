using System.ComponentModel.DataAnnotations;

namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Settings for URLs the embedded WebView2 surfaces may load directly.
/// Kept separate from <see cref="CopilotOptions.AllowedUrlHosts"/> so UI browsing
/// does not widen Copilot's automatic URL permission surface.
/// </summary>
public sealed class WebViewOptions
{
    public const string SectionName = "WebView";

    /// <summary>HTTPS hosts that the WebView2 resource filter should allow.</summary>
    [Required]
    [MinLength(1)]
    public IReadOnlyList<string> AllowedUrlHosts { get; set; } =
    [
        "docs.github.com",
        "github.com",
        "github.githubassets.com",
        "avatars.githubusercontent.com",
        "api.githubcopilot.com",
        "api.enterprise.githubcopilot.com",
    ];
}