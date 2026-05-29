namespace RepoSyncRadar.Core.Services;

/// <summary>
/// High-level entry points for the GitHub Copilot SDK driven agent sessions.
/// </summary>
/// <remarks>
/// The concrete implementation in <c>RepoSyncRadar.App</c> owns a single <c>CopilotClient</c> and
/// fans out per-task sessions. Custom <c>radar_*</c> tools are registered when each session is
/// created. See DESIGN.md §6-§8 for the tool catalog, session designs, and permission policy.
/// </remarks>
public interface ICopilotAgent
{
    /// <summary>Runs the morning triage session — fetch, score, summarise.</summary>
    Task<IngestionReport> RunMorningTriageAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the morning triage session and reports coarse-grained UI progress.</summary>
    Task<IngestionReport> RunMorningTriageAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Generates the sharing drafts and detailed diff explanation for a single focused commit.</summary>
    Task<DraftBundle> GenerateDraftsAsync(string commitSha, CancellationToken cancellationToken = default);

    /// <summary>Generates per-commit Japanese explanations and sharing drafts for multiple focused commits.</summary>
    Task<int> GenerateBatchExplanationAsync(
        IReadOnlyList<string> commitShas,
        CancellationToken cancellationToken = default);

}

/// <summary>
/// Focused-commit draft bundle. Each member is the raw body the user can copy or edit.
/// </summary>
public sealed record DraftBundle(string TwitterJa, string TeamsJa, string CustomerJa, string ExplanationJa);
