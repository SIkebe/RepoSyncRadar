using RepoSyncRadar.Core.Services;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Composite <see cref="ICopilotAgent"/> implementation that delegates to per-session
/// classes (<see cref="MorningTriageSession"/> and <see cref="AdoptionSession"/>).
/// </summary>
public sealed class CopilotAgent : ICopilotAgent
{
    private readonly MorningTriageSession _triage;
    private readonly AdoptionSession _adoption;

    public CopilotAgent(MorningTriageSession triage, AdoptionSession adoption)
    {
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(adoption);
        _triage = triage;
        _adoption = adoption;
    }

    public async Task<IngestionReport> RunMorningTriageAsync(CancellationToken cancellationToken = default)
    {
        return await _triage.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IngestionReport> RunMorningTriageAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        return await _triage.RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<TriageRunResult> RunMorningTriageWithResultAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        return _triage.RunDetailedAsync(progress, cancellationToken);
    }

    public Task<DraftBundle> GenerateDraftsAsync(string commitSha, CancellationToken cancellationToken = default)
    {
        return _adoption.GenerateDraftsAsync(commitSha, cancellationToken);
    }

    public Task<int> GenerateBatchExplanationAsync(
        IReadOnlyList<string> commitShas,
        CancellationToken cancellationToken = default)
    {
        return _adoption.GenerateBatchExplanationAsync(commitShas, cancellationToken);
    }
}
