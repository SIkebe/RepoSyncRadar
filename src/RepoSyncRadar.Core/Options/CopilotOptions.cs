using System.ComponentModel.DataAnnotations;

namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Settings for the embedded Copilot CLI agent runtime.
/// </summary>
public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";

    /// <summary>Override the bundled Copilot CLI binary. Null means use the SDK default.</summary>
    public string? CliPath { get; set; }

    /// <summary>Default model id (e.g. <c>gpt-5</c>, <c>claude-sonnet-4.5</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string DefaultModel { get; set; } = "gpt-5";

    /// <summary>Enable streaming response chunks. Recommended for the UI.</summary>
    public bool Streaming { get; set; } = true;

    /// <summary>Optional SDK context tier override. Use null for SDK/model default behavior.</summary>
    [RegularExpression("^(default|long_context)$")]
    public string? ContextTier { get; set; }

    /// <summary>Log level passed to the embedded Copilot CLI server.</summary>
    public string LogLevel { get; set; } = "info";

    /// <summary>
    /// Server-wide idle timeout for SDK sessions in seconds. Null or 0 leaves the SDK default.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int? SessionIdleTimeoutSeconds { get; set; }

    /// <summary>Optional base directory for Copilot CLI session state. Null uses the SDK default.</summary>
    public string? CopilotHome { get; set; }

    /// <summary>Where to write OpenTelemetry trace lines. Null disables file telemetry.</summary>
    public string? TelemetryFilePath { get; set; }

    /// <summary>Whether telemetry should include message content. Default off for privacy.</summary>
    public bool CaptureContent { get; set; }

    /// <summary>Enable SDK remote session URL support. Default off because installed builds may not run from a GitHub repository.</summary>
    public bool EnableRemoteSessions { get; set; }

    /// <summary>Enable Copilot's internal per-session telemetry. Set false to opt out.</summary>
    public bool EnableSessionTelemetry { get; set; } = true;

    /// <summary>Hosts that <c>url</c> permission requests may target without UI confirmation.</summary>
    [Required]
    [MinLength(1)]
    public IReadOnlyList<string> AllowedUrlHosts { get; set; } =
    [
        "docs.github.com",
        "api.github.com",
    ];

    /// <summary>
    /// Client ID of the GitHub OAuth (or GitHub) App used for the in-app sign-in device
    /// flow. Release builds should ship a public, non-secret default client ID; local
    /// settings or <c>RADAR_Copilot__OAuthClientId</c> may override it for organization
    /// managed OAuth Apps and forks. When this is null or whitespace the device flow is
    /// disabled and the session factory falls back to the optional
    /// <c>COPILOT_GITHUB_TOKEN</c> environment variable (intended for CI/debug).
    /// </summary>
    public string? OAuthClientId { get; set; }

    /// <summary>
    /// Scopes requested during the GitHub OAuth device flow. Default is empty, which
    /// asks for a token bound to the user's Copilot subscription only. Add
    /// <c>read:user</c> if the app needs to display the signed-in handle.
    /// </summary>
    public IReadOnlyList<string> OAuthScopes { get; set; } = [];
}
