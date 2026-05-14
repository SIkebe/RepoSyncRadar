using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Default <see cref="IProcessRunner"/> implementation backed by
/// <see cref="System.Diagnostics.Process"/>. Stdout/stderr are buffered into
/// <see cref="StringBuilder"/> so callers can include them in errors.
/// </summary>
/// <remarks>
/// Both <see cref="RunAsync"/> and <see cref="Start"/> wrap <see cref="Win32Exception"/>
/// thrown by <see cref="Process.Start(ProcessStartInfo)"/> (typically "the system cannot
/// find the file specified" when <c>git</c> or <c>npm</c> is missing from PATH) into a
/// uniform <see cref="InvalidOperationException"/>. This lets the Blazor UI catch a
/// single exception type and surface a friendly status without the WPF host process
/// terminating from an unhandled exception.
/// </remarks>
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

        Process p;
        try
        {
            p = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"'{fileName}' を起動できませんでした。PATH に追加されているか確認してください ({ex.Message})",
                ex);
        }

        using (p)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessRunResult(p.ExitCode, stdout.ToString(), stderr.ToString());
        }
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
        Process p;
        try
        {
            p = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"'{fileName}' を起動できませんでした。PATH に追加されているか確認してください ({ex.Message})",
                ex);
        }
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
