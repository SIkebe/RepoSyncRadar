using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoSyncRadar.Core.Services.Preview;


/// <summary>
/// Default <see cref="IProcessRunner"/> implementation backed by
/// <see cref="System.Diagnostics.Process"/>. Stdout/stderr are buffered into
/// <see cref="StringBuilder"/> so callers can include them in errors, and
/// long-running children additionally tee each line into <see cref="ILogger"/>
/// so failures surface in the app's log file instead of vanishing.
/// </summary>
/// <remarks>
/// Both <see cref="RunAsync"/> and <see cref="Start(string, string, string, System.Collections.Generic.IReadOnlyDictionary{string, string?}?)"/>
/// wrap <see cref="Win32Exception"/>
/// thrown by <see cref="Process.Start(ProcessStartInfo)"/> (typically "the system cannot
/// find the file specified" when <c>git</c> is missing from PATH) into a
/// uniform <see cref="InvalidOperationException"/>. This lets the Blazor UI catch a
/// single exception type and surface a friendly status without the WPF host process
/// terminating from an unhandled exception.
/// </remarks>
public sealed partial class SystemProcessRunner : IProcessRunner
{
    private static readonly ConcurrentDictionary<string, string> _resolvedExecutableCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly ILogger<SystemProcessRunner> _logger;

    public SystemProcessRunner()
        : this(NullLogger<SystemProcessRunner>.Instance)
    {
    }

    public SystemProcessRunner(ILogger<SystemProcessRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

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
            try
            {
                await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // already exited between HasExited and Kill
                }
                throw;
            }
            return new ProcessRunResult(p.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }

    public IProcessHandle Start(
        string fileName,
        string arguments,
        string workingDirectory,
        System.Collections.Generic.IReadOnlyDictionary<string, string?>? environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var psi = BuildStartInfo(fileName, arguments, workingDirectory, environment);
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
        return new ProcessHandle(p, _logger, fileName);
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> used by <see cref="Start(string, string, string, System.Collections.Generic.IReadOnlyDictionary{string, string?}?)"/>.
    /// Extracted so unit tests can verify <c>PATH</c> resolution, environment
    /// merging, and the standard set of redirect flags without spawning a real
    /// child process.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string fileName,
        string arguments,
        string workingDirectory,
        System.Collections.Generic.IReadOnlyDictionary<string, string?>? environment)
    {
        var resolved = ResolveOnPath(fileName);
        var psi = new ProcessStartInfo(resolved, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // See RunAsync for rationale — force UTF-8 decoding so child
            // process diagnostics with non-ASCII characters survive the
            // pipe intact.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                if (value is null)
                {
                    psi.Environment.Remove(key);
                }
                else
                {
                    psi.Environment[key] = value;
                }
            }
        }
        return psi;
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

    private sealed class ProcessHandle : IProcessHandle
    {
        // 64 lines × ~200 chars ≈ 12 KB upper bound per stream. Enough to catch
        // a typical child process stack trace without unbounded growth for
        // children that may run for hours.
        private const int _maxBufferedLines = 64;

        private readonly Process _process;
        private readonly ILogger _logger;
        private readonly string _label;
        private readonly object _stdoutGate = new();
        private readonly object _stderrGate = new();
        private readonly List<string> _stdoutBuffer = new(_maxBufferedLines);
        private readonly List<string> _stderrBuffer = new(_maxBufferedLines);

        public ProcessHandle(Process process, ILogger logger, string label)
        {
            _process = process;
            _logger = logger;
            _label = label;
            _process.OutputDataReceived += OnStdout;
            _process.ErrorDataReceived += OnStderr;
            // Without BeginOutputReadLine / BeginErrorReadLine the OS pipes fill up
            // quickly for chatty children and the child eventually blocks on its next
            // write. Always drain.
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public int ProcessId => _process.Id;

        public bool HasExited => _process.HasExited;

        public IReadOnlyList<string> RecentStdoutLines
        {
            get { lock (_stdoutGate) { return _stdoutBuffer.ToArray(); } }
        }

        public IReadOnlyList<string> RecentStderrLines
        {
            get { lock (_stderrGate) { return _stderrBuffer.ToArray(); } }
        }

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
            _process.OutputDataReceived -= OnStdout;
            _process.ErrorDataReceived -= OnStderr;
            _process.Dispose();
        }

        private void OnStdout(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) { return; }
            AppendBuffered(_stdoutBuffer, _stdoutGate, e.Data, _maxBufferedLines);
            LogStdout(_logger, _label, e.Data);
        }

        private void OnStderr(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) { return; }
            AppendBuffered(_stderrBuffer, _stderrGate, e.Data, _maxBufferedLines);
            LogStderr(_logger, _label, e.Data);
        }
    }

    // Marker used to collapse consecutive identical lines into a single
    // "(x N)" tail. Chosen because ASCII " (x N)" is unambiguous and renders correctly
    // regardless of the consumer's locale or font support.
    internal const string RepeatPrefix = " (x ";
    internal const string RepeatSuffix = ")";

    /// <summary>
    /// Appends <paramref name="line"/> to <paramref name="buffer"/>, but
    /// collapses runs of identical consecutive lines into a single
    /// <c>"&lt;line&gt; (x N)"</c> tail. Repeated child-process warnings would
    /// otherwise flood the 64-line ring buffer and push the real failure cause
    /// out before the UI samples it. Internal so unit tests can exercise the
    /// collapse logic without a real Process.
    /// </summary>
    internal static void AppendBuffered(List<string> buffer, object gate, string line, int maxBufferedLines)
    {
        lock (gate)
        {
            if (buffer.Count > 0)
            {
                var tail = buffer[^1];
                if (string.Equals(tail, line, StringComparison.Ordinal))
                {
                    buffer[^1] = line + RepeatPrefix + "2" + RepeatSuffix;
                    return;
                }
                if (TryParseRepeat(tail, out var baseLine, out var count)
                    && string.Equals(baseLine, line, StringComparison.Ordinal))
                {
                    buffer[^1] = baseLine + RepeatPrefix
                        + (count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + RepeatSuffix;
                    return;
                }
            }
            if (buffer.Count >= maxBufferedLines)
            {
                buffer.RemoveAt(0);
            }
            buffer.Add(line);
        }
    }

    /// <summary>
    /// Parses tails of the form <c>"&lt;baseLine&gt; (x N)"</c> produced by
    /// <see cref="AppendBuffered"/> itself. Returns false for anything that
    /// does not match exactly so unrelated lines that happen to contain
    /// "(x " in the middle are not mistakenly mutated. Internal for tests.
    /// </summary>
    internal static bool TryParseRepeat(string tail, out string baseLine, out int count)
    {
        baseLine = string.Empty;
        count = 0;
        if (!tail.EndsWith(RepeatSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        var idx = tail.LastIndexOf(RepeatPrefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            return false;
        }
        var numStart = idx + RepeatPrefix.Length;
        var numLen = tail.Length - numStart - RepeatSuffix.Length;
        if (numLen <= 0)
        {
            return false;
        }
        var span = tail.AsSpan(numStart, numLen);
        if (!int.TryParse(span, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < 2)
        {
            return false;
        }
        baseLine = tail[..idx];
        count = parsed;
        return true;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "[{Process}] {Line}")]
    private static partial void LogStdout(ILogger logger, string process, string line);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "[{Process}:err] {Line}")]
    private static partial void LogStderr(ILogger logger, string process, string line);
}
