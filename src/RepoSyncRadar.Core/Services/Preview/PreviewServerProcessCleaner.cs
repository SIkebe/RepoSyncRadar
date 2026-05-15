using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RepoSyncRadar.Core.Services.Preview;

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
        var logLastWriteUtc = TryGetLastWriteUtc(logPath);
        var candidatePids = FindCandidatePids(fullWorktreePath, startupFailureOutput);
        var stopped = 0;

        foreach (var pid in candidatePids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryStopProcessAsync(pid, fullWorktreePath, logLastWriteUtc, cancellationToken)
                    .ConfigureAwait(false))
            {
                stopped++;
            }
        }

        return stopped;
    }

    internal static IReadOnlyList<int> FindCandidatePids(string worktreePath, string? startupFailureOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);

        var fullWorktreePath = Path.GetFullPath(worktreePath);
        var result = new HashSet<int>();
        var logPath = GetNextDevelopmentLogPath(fullWorktreePath);
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

        return result.Order().ToArray();
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
            if (int.TryParse(
                    match.Groups["pid"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pid)
                && pid > 0)
            {
                result.Add(pid);
            }
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