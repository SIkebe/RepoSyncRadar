namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Thin wrapper around <see cref="System.Diagnostics.Process"/> so that the
/// preview pipeline can be unit-tested without spawning real processes. Two flavors:
/// <list type="bullet">
/// <item><see cref="RunAsync"/> — fire-and-wait, captures stdout/stderr (used for <c>git</c> commands).</item>
/// <item><see cref="Start(string, string, string, IReadOnlyDictionary{string, string?}?)"/>
/// — long-running child process; the caller owns the handle.</item>
/// </list>
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Backwards-compatible overload that forwards to the env-aware version with no overrides.</summary>
    IProcessHandle Start(
        string fileName,
        string arguments,
        string workingDirectory)
        => Start(fileName, arguments, workingDirectory, environment: null);

    /// <summary>
    /// Starts <paramref name="fileName"/> as a long-running child process with the
    /// supplied environment overrides merged on top of the parent process'
    /// environment.
    /// </summary>
    IProcessHandle Start(
        string fileName,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment);
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

    /// <summary>
    /// Most recent lines written to the child's <c>stdout</c>, oldest first.
    /// Default implementation returns an empty list so test doubles do not need
    /// to implement the buffer. Production implementations should cap the
    /// buffer (ring buffer) to keep memory bounded for chatty children.
    /// </summary>
    IReadOnlyList<string> RecentStdoutLines => Array.Empty<string>();

    /// <summary>
    /// Most recent lines written to the child's <c>stderr</c>, oldest first.
    /// Implementations should keep this bounded for long-running children.
    /// </summary>
    IReadOnlyList<string> RecentStderrLines => Array.Empty<string>();
}
