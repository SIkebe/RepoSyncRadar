using GitHub.Copilot.SDK;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Creates Copilot sessions for the various app workflows. The factory owns at most
/// one underlying <see cref="CopilotClient"/> per process — callers must
/// <see cref="IAsyncDisposable.DisposeAsync"/> the factory at app shutdown so the
/// embedded CLI process is reaped.
/// </summary>
public interface ICopilotSessionFactory : IAsyncDisposable
{
    Task<CopilotSession> CreateSessionAsync(
        SessionPurpose purpose,
        CancellationToken cancellationToken = default);
}
