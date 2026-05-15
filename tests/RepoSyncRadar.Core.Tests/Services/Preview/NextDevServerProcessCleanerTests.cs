using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

public sealed class NextDevServerProcessCleanerTests : IDisposable
{
    private readonly string _tempRoot;

    public NextDevServerProcessCleanerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "rsr-next-cleaner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void FindCandidatePids_Reads_Server_Started_Pids_From_Next_Log()
    {
        var logDir = Path.Combine(_tempRoot, ".next", "dev", "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, "next-development.log"),
            "{\"message\":\"Server started  port=4501 pid=32776 nodeEnv=development\"}" + Environment.NewLine);

        var pids = NextDevServerProcessCleaner.FindCandidatePids(_tempRoot, startupFailureOutput: null);

        Assert.Equal(32776, Assert.Single(pids));
    }

    [Fact]
    public void FindCandidatePids_Reads_Duplicate_Server_Pid_When_Dir_Matches()
    {
        var message = "× Another next dev server is already running. - Local: http://localhost:3000 - PID: 32776 - Dir: "
            + _tempRoot
            + " - Log: .next\\dev\\logs\\next-development.log Run taskkill /PID 32776 /F to stop it.";

        var pids = NextDevServerProcessCleaner.FindCandidatePids(_tempRoot, message);

        Assert.Equal(32776, Assert.Single(pids));
    }

    [Fact]
    public void FindCandidatePids_Ignores_Duplicate_Server_Pid_When_Dir_Differs()
    {
        var otherDir = Path.Combine(Path.GetTempPath(), "other-worktree");
        var message = "× Another next dev server is already running. - Local: http://localhost:3000 - PID: 32776 - Dir: "
            + otherDir
            + " - Log: .next\\dev\\logs\\next-development.log Run taskkill /PID 32776 /F to stop it.";

        var pids = NextDevServerProcessCleaner.FindCandidatePids(_tempRoot, message);

        Assert.Empty(pids);
    }

    [Fact]
    public void IsDuplicateNextDevServerMessage_Detects_Next_Error()
    {
        Assert.True(NextDevServerProcessCleaner.IsDuplicateNextDevServerMessage(
            "× Another next dev server is already running. - PID: 32776 - Dir: C:\\wt - Log: .next\\dev\\logs\\next-development.log"));
        Assert.False(NextDevServerProcessCleaner.IsDuplicateNextDevServerMessage(
            "EADDRINUSE: address already in use :::4500"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}