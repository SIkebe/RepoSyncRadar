namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Creates Copilot sessions for the various app workflows. The factory owns at most
/// one underlying <c>CopilotClient</c> per process — callers must
/// <see cref="IAsyncDisposable.DisposeAsync"/> the factory at app shutdown so the
/// in-process runtime shuts down cleanly.
/// </summary>
public interface ICopilotSessionFactory : IAsyncDisposable
{
    Task<ICopilotSession> CreateSessionAsync(
        SessionPurpose purpose,
        CancellationToken cancellationToken = default);
}
