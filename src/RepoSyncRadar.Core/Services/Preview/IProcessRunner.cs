namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Thin wrapper around <see cref="System.Diagnostics.Process"/> so that the
/// preview pipeline (IMPLEMENTATION_PLAN.md §Step 19) can be unit-tested without
/// spawning real processes. Two flavors:
/// <list type="bullet">
/// <item><see cref="RunAsync"/> — fire-and-wait, captures stdout/stderr (used for <c>git</c> commands).</item>
/// <item><see cref="Start(string, string, string, IReadOnlyDictionary{string, string?}?)"/>
/// — long-running sidecar (used for the docs <c>npm run dev</c> server); the caller owns the handle.</item>
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
    /// Starts <paramref name="fileName"/> as a long-running sidecar with the
    /// supplied environment overrides merged on top of the parent process'
    /// environment. Needed because some preview targets (notably
    /// <c>github/docs</c>) honor <c>PORT</c> only via environment variables and
    /// ignore command-line <c>--port</c> flags.
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
    /// <see cref="PreviewServerHost"/> surfaces these in the
    /// <see cref="InvalidOperationException"/> it throws on startup failure so
    /// the UI can show <c>cross-env: not found</c> / <c>ENOENT node_modules</c>
    /// instead of the opaque "did not become ready" message.
    /// </summary>
    IReadOnlyList<string> RecentStderrLines => Array.Empty<string>();
}
