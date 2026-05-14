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

        var resolved = ResolveOnPath(fileName);
        var psi = new ProcessStartInfo(resolved, arguments)
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

        var resolved = ResolveOnPath(fileName);
        var psi = new ProcessStartInfo(resolved, arguments)
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

    /// <summary>
    /// Looks up <paramref name="fileName"/> on the real process PATH/PATHEXT.
    /// Wraps <see cref="ResolveExecutable(string, string?, string?, Func{string, bool}?)"/>
    /// so production code stays simple while tests can inject their own filesystem.
    /// </summary>
    private static string ResolveOnPath(string fileName)
        => ResolveExecutable(
            fileName,
            pathEnv: Environment.GetEnvironmentVariable("PATH"),
            pathExtEnv: Environment.GetEnvironmentVariable("PATHEXT"),
            fileExists: File.Exists);

    /// <summary>
    /// Resolves a bare command name (e.g. <c>npm</c>) into a full path such as
    /// <c>C:\Program Files\nodejs\npm.cmd</c> by combining each <c>PATH</c> entry
    /// with each <c>PATHEXT</c> extension. Needed because the Win32
    /// <c>CreateProcess</c> API (used when <c>UseShellExecute = false</c>) only
    /// auto-appends <c>.exe</c>, so <c>.cmd</c>/<c>.bat</c> wrappers shipped by
    /// tools like <c>npm</c>, <c>yarn</c>, and <c>pnpm</c> are otherwise invisible.
    /// </summary>
    /// <param name="fileName">Command name passed by the caller. May be a bare name (<c>npm</c>), a
    /// name with extension (<c>npm.cmd</c>), or an absolute path.</param>
    /// <param name="pathEnv">Contents of the <c>PATH</c> environment variable. Entries are split on
    /// the platform path separator; surrounding quotes are tolerated.</param>
    /// <param name="pathExtEnv">Contents of the <c>PATHEXT</c> environment variable. Falls back to
    /// the Windows default (<c>.COM;.EXE;.BAT;.CMD</c>) on Windows when null/empty.</param>
    /// <param name="fileExists">Filesystem probe. Tests pass an in-memory set; production passes
    /// <see cref="File.Exists(string)"/>.</param>
    /// <returns>The resolved absolute path when found, otherwise <paramref name="fileName"/> unchanged
    /// so the eventual <c>Win32Exception</c> still surfaces the original name.</returns>
    internal static string ResolveExecutable(
        string fileName,
        string? pathEnv,
        string? pathExtEnv,
        Func<string, bool>? fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!OperatingSystem.IsWindows())
        {
            return fileName;
        }
        if (Path.IsPathRooted(fileName))
        {
            return fileName;
        }
        if (Path.HasExtension(fileName))
        {
            return fileName;
        }

        fileExists ??= File.Exists;
        var exts = SplitPathExt(pathExtEnv);
        if (exts.Length == 0)
        {
            return fileName;
        }

        foreach (var rawDir in (pathEnv ?? string.Empty).Split(Path.PathSeparator))
        {
            var dir = StripQuotes(rawDir).Trim();
            if (dir.Length == 0)
            {
                continue;
            }
            foreach (var ext in exts)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir, fileName + ext);
                }
                catch (ArgumentException)
                {
                    // PATH entries occasionally contain stray invalid chars on broken machines;
                    // skip those rather than failing the whole resolution.
                    break;
                }
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }

    private static string[] SplitPathExt(string? pathExtEnv)
    {
        var source = string.IsNullOrWhiteSpace(pathExtEnv)
            ? ".COM;.EXE;.BAT;.CMD"
            : pathExtEnv;
        var parts = source.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (!parts[i].StartsWith('.'))
            {
                parts[i] = "." + parts[i];
            }
        }
        return parts;
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }
        return value;
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
