using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RepoSyncRadar.App;
using Xunit;

namespace RepoSyncRadar.App.Tests;

/// <summary>
/// Tests for the global unhandled-exception sink wired up in
/// <see cref="App.OnStartup"/>. The sink must (a) log the exception, (b) surface a
/// dialog via the injected callback (so headless tests can omit the
/// <see cref="System.Windows.MessageBox"/> dependency), and (c) be safe to call with
/// a <c>null</c> exception (which happens when
/// <c>AppDomain.UnhandledException</c> fires with a non-<see cref="Exception"/>
/// payload).
/// </summary>
public sealed class UnhandledExceptionTests
{
    [Fact]
    public void HandleUnhandled_With_Exception_Logs_And_Shows_Dialog()
    {
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<App>>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);
        var dialogs = new List<string>();
        var ex = new InvalidOperationException("git show failed (exit 128)");

        App.HandleUnhandled(ex, logger, dialogs.Add);

        Assert.Single(dialogs);
        Assert.Contains("InvalidOperationException", dialogs[0], StringComparison.Ordinal);
        Assert.Contains("git show failed", dialogs[0], StringComparison.Ordinal);
        logger.Received().Log(
            Microsoft.Extensions.Logging.LogLevel.Error,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Any<object>(),
            ex,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void HandleUnhandled_With_Null_Exception_Is_NoOp()
    {
        var dialogs = new List<string>();

        App.HandleUnhandled(null, NullLogger<App>.Instance, dialogs.Add);

        Assert.Empty(dialogs);
    }

    [Fact]
    public void HandleUnhandled_With_Null_Logger_Does_Not_Throw()
    {
        var dialogs = new List<string>();

        App.HandleUnhandled(new InvalidOperationException("boom"), logger: null, dialogs.Add);

        Assert.Single(dialogs);
    }

    [Fact]
    public void HandleUnhandled_With_Null_ShowDialog_Does_Not_Throw()
    {
        // Dialogs can fail when the WPF Dispatcher is already torn down. The handler
        // must still log and return cleanly so the process can finish unwinding.
        App.HandleUnhandled(new InvalidOperationException("boom"), NullLogger<App>.Instance, showDialog: null);
    }

    [Fact]
    public void IsRenderThreadFailure_With_UceerrRenderThreadFailure_Returns_True()
    {
        var exception = Assert.IsType<COMException>(
            Marshal.GetExceptionForHR(unchecked((int)0x88980406)));
        var otherException = Assert.IsType<COMException>(
            Marshal.GetExceptionForHR(unchecked((int)0x80004005)));

        Assert.True(App.IsRenderThreadFailure(exception));
        Assert.False(App.IsRenderThreadFailure(otherException));
    }

    [Fact]
    public void HandleUnhandled_With_RenderThreadFailure_Explains_Recovery()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        var dialogs = new List<string>();
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var exception = Assert.IsType<COMException>(
                Marshal.GetExceptionForHR(unchecked((int)0x88980406)));

            App.HandleUnhandled(exception, NullLogger<App>.Instance, dialogs.Add);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }

        var message = Assert.Single(dialogs);
        Assert.Contains("will close", message, StringComparison.Ordinal);
        Assert.Contains("sleep/resume", message, StringComparison.Ordinal);
        Assert.Contains("graphics-driver updates", message, StringComparison.Ordinal);
    }

    [Fact]
    public void HandleRenderThreadFailure_RepeatedFailures_ReportAndShutdownOnce()
    {
        var exception = Assert.IsType<COMException>(
            Marshal.GetExceptionForHR(unchecked((int)0x88980406)));
        var handlingStarted = 0;
        var reports = new List<Exception>();
        var shutdownCount = 0;

        App.HandleRenderThreadFailure(exception, ref handlingStarted, reports.Add, () => shutdownCount++);
        App.HandleRenderThreadFailure(exception, ref handlingStarted, reports.Add, () => shutdownCount++);

        Assert.Same(exception, Assert.Single(reports));
        Assert.Equal(1, shutdownCount);
    }
}
