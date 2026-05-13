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

    /// <summary>Where to write OpenTelemetry trace lines. Null disables file telemetry.</summary>
    public string? TelemetryFilePath { get; set; }

    /// <summary>Whether telemetry should include message content. Default off for privacy.</summary>
    public bool CaptureContent { get; set; }

    /// <summary>Hosts that <c>url</c> permission requests may target without UI confirmation.</summary>
    [Required]
    [MinLength(1)]
    public IReadOnlyList<string> AllowedUrlHosts { get; set; } =
    [
        "docs.github.com",
        "api.github.com",
    ];
}
