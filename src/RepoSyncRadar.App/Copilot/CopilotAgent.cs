using RepoSyncRadar.Core.Services;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Composite <see cref="ICopilotAgent"/> implementation that delegates to per-session
/// classes (<see cref="MorningTriageSession"/>, <see cref="AdoptionSession"/>). The Ask
/// session lands in IMPLEMENTATION_PLAN.md §Step 18.
/// </summary>
public sealed class CopilotAgent : ICopilotAgent
{
    private readonly MorningTriageSession _triage;
    private readonly AdoptionSession _adoption;
    private readonly AskSession _ask;

    public CopilotAgent(MorningTriageSession triage, AdoptionSession adoption, AskSession ask)
    {
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(adoption);
        ArgumentNullException.ThrowIfNull(ask);
        _triage = triage;
        _adoption = adoption;
        _ask = ask;
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

    public Task<string> AskAsync(string naturalLanguageQuery, CancellationToken cancellationToken = default)
        => _ask.AskAsync(naturalLanguageQuery, debug: false, cancellationToken);
}
