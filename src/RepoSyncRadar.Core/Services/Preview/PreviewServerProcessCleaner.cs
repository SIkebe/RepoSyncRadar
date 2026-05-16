using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RepoSyncRadar.Core.Services.Preview;

internal readonly record struct PreviewProcessSnapshot(int ProcessId, string ProcessName, string? CommandLine);

public interface IPreviewServerProcessCleaner
{
    Task<int> StopStaleServersAsync(
        string worktreePath,
        string? startupFailureOutput = null,
        CancellationToken cancellationToken = default);
}

public sealed class NoopPreviewServerProcessCleaner : IPreviewServerProcessCleaner
{
    public static NoopPreviewServerProcessCleaner Instance { get; } = new();

    private NoopPreviewServerProcessCleaner()
    {
    }

    public Task<int> StopStaleServersAsync(
        string worktreePath,
        string? startupFailureOutput = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}

public sealed partial class NextDevServerProcessCleaner(ILogger<NextDevServerProcessCleaner> logger)
    : IPreviewServerProcessCleaner
{
    private const string _previewPidFileName = ".reposyncradar-preview-pids";
    private const int _maxLogLinesToInspect = 200;
    private static readonly Regex _serverStartedPidRegex = new(
        @"\bServer started\s+port=\d+\s+pid=(?<pid>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _duplicateServerPidRegex = new(
        @"\bPID:\s*(?<pid>\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _duplicateServerDirRegex = new(
        @"\bDir:\s*(?<dir>.+?)\s+-\s+Log:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<int> StopStaleServersAsync(
        string worktreePath,
        string? startupFailureOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        var fullWorktreePath = Path.GetFullPath(worktreePath);
        var logPath = GetNextDevelopmentLogPath(fullWorktreePath);
        var pidFilePath = GetPreviewPidFilePath(fullWorktreePath);
        var guardLastWriteUtc = Latest(
            TryGetLastWriteUtc(logPath),
            TryGetLastWriteUtc(pidFilePath));
        var candidatePids = FindCandidatePids(fullWorktreePath, startupFailureOutput);
        var stopped = 0;

        foreach (var pid in candidatePids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryStopProcessAsync(pid, fullWorktreePath, guardLastWriteUtc, cancellationToken)
                    .ConfigureAwait(false))
            {
                stopped++;
            }
        }

        return stopped;
    }

    internal static IReadOnlyList<int> FindCandidatePids(string worktreePath, string? startupFailureOutput)
        => FindCandidatePids(worktreePath, startupFailureOutput, EnumerateNodeProcessSnapshots());

    internal static IReadOnlyList<int> FindCandidatePids(
        string worktreePath,
        string? startupFailureOutput,
        IEnumerable<PreviewProcessSnapshot> processSnapshots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(processSnapshots);

        var fullWorktreePath = Path.GetFullPath(worktreePath);
        var result = new HashSet<int>();
        var logPath = GetNextDevelopmentLogPath(fullWorktreePath);
        var pidFilePath = GetPreviewPidFilePath(fullWorktreePath);
        if (File.Exists(pidFilePath))
        {
            foreach (var line in ReadTailLines(pidFilePath, _maxLogLinesToInspect))
            {
                AddPid(line, result);
            }
        }

        if (File.Exists(logPath))
        {
            foreach (var line in ReadTailLines(logPath, _maxLogLinesToInspect))
            {
                AddMatches(_serverStartedPidRegex, line, result);
            }
        }

        if (!string.IsNullOrWhiteSpace(startupFailureOutput)
            && MentionsSameWorktree(startupFailureOutput, fullWorktreePath))
        {
            AddMatches(_duplicateServerPidRegex, startupFailureOutput, result);
        }

        foreach (var snapshot in processSnapshots)
        {
            if (snapshot.ProcessId > 0
                && IsNodeProcess(snapshot.ProcessName)
                && CommandLineMentionsWorktree(snapshot.CommandLine, fullWorktreePath))
            {
                result.Add(snapshot.ProcessId);
            }
        }

        return result.Order().ToArray();
    }

    internal static bool CommandLineMentionsWorktree(string? commandLine, string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var normalizedCommandLine = NormalizeForPathSearch(commandLine);
        var normalizedWorktreePath = NormalizeForPathSearch(Path.GetFullPath(worktreePath))
            .TrimEnd('/');
        return normalizedCommandLine.Contains(normalizedWorktreePath, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MentionsSameWorktree(string text, string worktreePath)
    {
        var dirMatch = _duplicateServerDirRegex.Match(text);
        if (!dirMatch.Success)
        {
            return true;
        }

        var reportedDir = dirMatch.Groups["dir"].Value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(reportedDir))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(reportedDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsDuplicateNextDevServerMessage(string text)
        => text.Contains("Another next dev server is already running", StringComparison.OrdinalIgnoreCase)
            && _duplicateServerPidRegex.IsMatch(text);

    internal static void RememberPreviewProcess(string worktreePath, int processId)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || processId <= 0)
        {
            return;
        }

        try
        {
            var pidFilePath = GetPreviewPidFilePath(Path.GetFullPath(worktreePath));
            File.AppendAllText(
                pidFilePath,
                processId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine,
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private async Task<bool> TryStopProcessAsync(
        int pid,
        string worktreePath,
        DateTimeOffset? logLastWriteUtc,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return false;
        }
        using (process)
        {
            try
            {
                if (process.HasExited || !IsNodeProcess(process.ProcessName))
                {
                    return false;
                }

                if (logLastWriteUtc is { } lastWriteUtc && TryGetStartTimeUtc(process) is { } startTimeUtc
                    && startTimeUtc > lastWriteUtc.AddSeconds(5))
                {
                    LogPidReuseSkipped(logger, pid, worktreePath, startTimeUtc, lastWriteUtc);
                    return false;
                }

                LogStoppingStaleNextDev(logger, pid, worktreePath);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception ex)
            {
                LogStopFailed(logger, ex, pid, worktreePath);
                return false;
            }
            catch (TimeoutException ex)
            {
                LogStopFailed(logger, ex, pid, worktreePath);
                return false;
            }
        }
    }

    private static bool IsNodeProcess(string processName)
        => string.Equals(processName, "node", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, "node.exe", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForPathSearch(string value)
        => value.Replace('\\', '/');

    private static IReadOnlyList<PreviewProcessSnapshot> EnumerateNodeProcessSnapshots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<PreviewProcessSnapshot>();
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-Command",
                    "Get-CimInstance Win32_Process -Filter \"Name = 'node.exe'\" | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress",
                },
            });
            if (process is null)
            {
                return Array.Empty<PreviewProcessSnapshot>();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 5000))
            {
                TryKillProcessTree(process);
                return Array.Empty<PreviewProcessSnapshot>();
            }

            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return Array.Empty<PreviewProcessSnapshot>();
            }

            return ParseWindowsProcessJson(output);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<PreviewProcessSnapshot>();
        }
    }

    private static IReadOnlyList<PreviewProcessSnapshot> ParseWindowsProcessJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var results = new List<PreviewProcessSnapshot>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    AddProcessSnapshot(element, results);
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddProcessSnapshot(document.RootElement, results);
            }

            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<PreviewProcessSnapshot>();
        }
    }

    private static void AddProcessSnapshot(JsonElement element, List<PreviewProcessSnapshot> results)
    {
        if (!element.TryGetProperty("ProcessId", out var pidElement)
            || !pidElement.TryGetInt32(out var pid)
            || pid <= 0)
        {
            return;
        }

        var name = element.TryGetProperty("Name", out var nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        var commandLine = element.TryGetProperty("CommandLine", out var commandLineElement)
            ? commandLineElement.GetString()
            : null;
        results.Add(new PreviewProcessSnapshot(pid, name, commandLine));
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private static DateTimeOffset? TryGetStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryGetLastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddMatches(Regex regex, string text, HashSet<int> result)
    {
        foreach (Match match in regex.Matches(text))
        {
            AddPid(match.Groups["pid"].Value, result);
        }
    }

    private static void AddPid(string text, HashSet<int> result)
    {
        if (int.TryParse(
                text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var pid)
            && pid > 0)
        {
            result.Add(pid);
        }
    }

    private static string[] ReadTailLines(string path, int maxLines)
    {
        var lines = new Queue<string>(maxLines);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == maxLines)
                {
                    lines.Dequeue();
                }
                lines.Enqueue(line);
            }
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        return lines.ToArray();
    }

    private static string GetNextDevelopmentLogPath(string worktreePath)
        => Path.Combine(worktreePath, ".next", "dev", "logs", "next-development.log");

    private static string GetPreviewPidFilePath(string worktreePath)
        => Path.Combine(worktreePath, _previewPidFileName);

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }
        if (right is null)
        {
            return left;
        }
        return left > right ? left : right;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Stopping stale Next dev server PID {Pid} for worktree {WorktreePath}.")]
    private static partial void LogStoppingStaleNextDev(ILogger logger, int pid, string worktreePath);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Skipping stale Next dev PID {Pid} for worktree {WorktreePath}; process start {StartTimeUtc} is newer than log {LogLastWriteUtc}.")]
    private static partial void LogPidReuseSkipped(
        ILogger logger,
        int pid,
        string worktreePath,
        DateTimeOffset startTimeUtc,
        DateTimeOffset logLastWriteUtc);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Failed to stop stale Next dev server PID {Pid} for worktree {WorktreePath}.")]
    private static partial void LogStopFailed(ILogger logger, Exception exception, int pid, string worktreePath);
}