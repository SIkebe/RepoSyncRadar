using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Integrations.Tests.Preview;

/// <summary>
/// Tests for <see cref="SystemProcessRunner"/>. The crucial behaviour for the UI is
/// that <see cref="System.ComponentModel.Win32Exception"/> from <c>Process.Start</c>
/// (e.g. "the system cannot find the file specified" when <c>git</c> / <c>npm</c>
/// is not on PATH) gets wrapped into an <see cref="InvalidOperationException"/>
/// so the calling Blazor component can show a friendly status message instead of
/// the WPF host crashing with an unhandled exception.
/// </summary>
public sealed class SystemProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_When_FileName_Missing_Throws_InvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new SystemProcessRunner();
        const string missing = "rsr_nonexistent_binary_for_test_zzz";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(missing, string.Empty, Path.GetTempPath(), ct));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Start_When_FileName_Missing_Throws_InvalidOperationException()
    {
        IProcessRunner sut = new SystemProcessRunner();
        const string missing = "rsr_nonexistent_binary_for_test_zzz";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            sut.Start(missing, string.Empty, Path.GetTempPath()));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Start_Captures_Recent_Stdout_And_Stderr_From_Child()
    {
        // We need this to be reliable on Windows (the project's only target).
        // `cmd /c echo ... & echo ... 1>&2` exits immediately and writes one line
        // to each stream — exactly the surface PreviewServerHost queries when
        // emitting "なぜ起動できなかったか" to the UI.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IProcessRunner sut = new SystemProcessRunner();
        var handle = sut.Start(
            "cmd",
            "/c echo hello-stdout & echo hello-stderr 1>&2",
            Path.GetTempPath());
        try
        {
            await handle.WaitForExitAsync(TestContext.Current.CancellationToken);
            // OutputDataReceived runs on a background thread; give it a brief
            // window to flush the final lines before sampling the ring buffer.
            for (var i = 0; i < 20; i++)
            {
                if (handle.RecentStdoutLines.Count > 0 && handle.RecentStderrLines.Count > 0)
                {
                    break;
                }
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.Contains(handle.RecentStdoutLines, l => l.Contains("hello-stdout", StringComparison.Ordinal));
            Assert.Contains(handle.RecentStderrLines, l => l.Contains("hello-stderr", StringComparison.Ordinal));
        }
        finally
        {
            await handle.DisposeAsync();
        }
    }
}
