using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RepoSyncRadar.Core.Services.Preview;


/// <summary>
/// Default <see cref="IProcessRunner"/> implementation backed by
/// <see cref="System.Diagnostics.Process"/>. Stdout/stderr are captured together
/// so callers can include them in errors without risking pipe deadlocks.
/// </summary>
/// <remarks>
/// <see cref="RunAsync"/> wraps <see cref="Win32Exception"/> thrown by
/// <see cref="Process.Start(ProcessStartInfo)"/> (typically "the system cannot
/// find the file specified" when <c>git</c> is missing from PATH) into a uniform
/// <see cref="InvalidOperationException"/>. This lets the Blazor UI catch a single
/// exception type and surface a friendly status without the WPF host process
/// terminating from an unhandled exception.
/// </remarks>
public sealed class SystemProcessRunner : IProcessRunner
{
    private static readonly ConcurrentDictionary<string, string> _resolvedExecutableCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
            // git and other CLI tools often emit UTF-8 (including Unicode glyphs like
            // "⚠") regardless of the active Windows code page. Without
            // setting these explicitly, .NET decodes the redirected streams
            // using Console.OutputEncoding (CP932 on Japanese Windows), which
            // garbles diagnostics into "答口" style mojibake and makes
            // failure messages unreadable in the UI.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            var output = await Process.RunAndCaptureTextAsync(psi, cancellationToken).ConfigureAwait(false);
            return new ProcessRunResult(
                output.ExitStatus.ExitCode,
                output.StandardOutput,
                output.StandardError);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"'{fileName}' を起動できませんでした。PATH に追加されているか確認してください ({ex.Message})",
                ex);
        }
    }

    /// <summary>
    /// Looks up <paramref name="fileName"/> on the real process PATH/PATHEXT.
    /// Wraps <see cref="ResolveExecutable(string, string?, string?, Func{string, bool}?)"/>
    /// so production code stays simple while tests can inject their own filesystem.
    /// </summary>
    private static string ResolveOnPath(string fileName)
    {
        if (!OperatingSystem.IsWindows()
            || Path.IsPathRooted(fileName)
            || Path.HasExtension(fileName))
        {
            return fileName;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        var pathExtEnv = Environment.GetEnvironmentVariable("PATHEXT");
        var cacheKey = string.Concat(fileName, "\0", pathEnv, "\0", pathExtEnv);
        return _resolvedExecutableCache.GetOrAdd(
            cacheKey,
            static (_, state) => ResolveExecutable(
                state.FileName,
                state.PathEnv,
                state.PathExtEnv,
                File.Exists),
            (FileName: fileName, PathEnv: pathEnv, PathExtEnv: pathExtEnv));
    }

    /// <summary>
    /// Resolves a bare command name (e.g. <c>tool</c>) into a full path such as
    /// <c>C:\Tools\tool.cmd</c> by combining each <c>PATH</c> entry
    /// with each <c>PATHEXT</c> extension. Needed because the Win32
    /// <c>CreateProcess</c> API (used when <c>UseShellExecute = false</c>) only
    /// auto-appends <c>.exe</c>, so <c>.cmd</c>/<c>.bat</c> wrappers shipped by
    /// tools distributed as batch wrappers are otherwise invisible.
    /// </summary>
    /// <param name="fileName">Command name passed by the caller. May be a bare name (<c>tool</c>), a
    /// name with extension (<c>tool.cmd</c>), or an absolute path.</param>
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

}
