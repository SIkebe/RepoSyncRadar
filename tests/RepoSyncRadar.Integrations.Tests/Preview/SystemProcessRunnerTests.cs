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
        var sut = new SystemProcessRunner();
        const string missing = "rsr_nonexistent_binary_for_test_zzz";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            sut.Start(missing, string.Empty, Path.GetTempPath()));

        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }
}
