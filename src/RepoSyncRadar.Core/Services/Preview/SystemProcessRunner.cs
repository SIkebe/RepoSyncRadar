using System.Diagnostics;
using System.Text;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Default <see cref="IProcessRunner"/> implementation backed by
/// <see cref="System.Diagnostics.Process"/>. Stdout/stderr are buffered into
/// <see cref="StringBuilder"/> so callers can include them in errors.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessRunResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public IProcessHandle Start(string fileName, string arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        return new ProcessHandle(p);
    }

    private sealed class ProcessHandle : IProcessHandle
    {
        private readonly Process _process;

        public ProcessHandle(Process process)
        {
            _process = process;
        }

        public int ProcessId => _process.Id;

        public bool HasExited => _process.HasExited;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            return WaitInnerAsync(cancellationToken);
        }

        private async Task<int> WaitInnerAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public Task KillAsync(CancellationToken cancellationToken = default)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await KillAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // already exited — nothing to do
            }
            _process.Dispose();
        }
    }
}
