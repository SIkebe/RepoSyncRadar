namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Thin wrapper around <see cref="System.Diagnostics.Process"/> so that the
/// preview pipeline (IMPLEMENTATION_PLAN.md §Step 19) can be unit-tested without
/// spawning real processes. Two flavors:
/// <list type="bullet">
/// <item><see cref="RunAsync"/> — fire-and-wait, captures stdout/stderr (used for <c>git</c> commands).</item>
/// <item><see cref="Start"/> — long-running sidecar (used for <c>next dev</c>); the caller owns the handle.</item>
/// </list>
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    IProcessHandle Start(
        string fileName,
        string arguments,
        string workingDirectory);
}

/// <summary>Captured outcome of <see cref="IProcessRunner.RunAsync"/>.</summary>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Handle to a process started via <see cref="IProcessRunner.Start"/>.</summary>
public interface IProcessHandle : IAsyncDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
    Task KillAsync(CancellationToken cancellationToken = default);
}
