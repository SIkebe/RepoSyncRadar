namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Thin wrapper around <see cref="System.Diagnostics.Process"/> so that the
/// preview pipeline can be unit-tested without spawning real processes.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default);

}

/// <summary>Captured outcome of <see cref="IProcessRunner.RunAsync"/>.</summary>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);
