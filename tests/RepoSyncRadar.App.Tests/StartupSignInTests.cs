using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests;

/// <summary>
/// Behaviour tests for the eager startup sign-in helper invoked from
/// <see cref="App.OnStartup"/>. We isolate the testable core behind a
/// callback so the WPF <see cref="System.Windows.MessageBox"/> dependency
/// doesn't leak into headless test runs.
/// </summary>
public class StartupSignInTests
{
    [Fact]
    public async Task ConfigureAppConfiguration_Reads_Explicit_Local_Appsettings_Path()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "rsr-startup-config-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var basePath = Path.Combine(tempRoot, "app");
        Directory.CreateDirectory(basePath);
        var explicitLocalPath = Path.Combine(tempRoot, "shared", "appsettings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(explicitLocalPath)!);
        await File.WriteAllTextAsync(
            Path.Combine(basePath, "appsettings.json"),
            """
            {
              "Copilot": {
                "OAuthClientId": ""
              }
            }
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            explicitLocalPath,
            """
            {
              "Copilot": {
                "OAuthClientId": "Iv1.from-explicit-local"
              }
            }
            """,
            TestContext.Current.CancellationToken);
        try
        {
            var configuration = (IConfigurationRoot)App.ConfigureAppConfiguration(
                new ConfigurationBuilder(),
                basePath,
                explicitLocalPath).Build();

            Assert.Equal("Iv1.from-explicit-local", configuration["Copilot:OAuthClientId"]);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task TrySignInOnStartupAsync_WhenClientIdMissing_WarnsUserAndSkipsProvider()
    {
        // ClientId is the smoking gun for "I started the app and nothing happened" —
        // surface the configuration gap before the user clicks Ask/Adopt and gets a
        // mysterious InvalidOperationException buried in a Copilot stack trace.
        var provider = Substitute.For<IGitHubAccessTokenProvider>();
        var options = new CopilotOptions { OAuthClientId = string.Empty };
        var warnings = new List<string>();

        await App.TrySignInOnStartupAsync(
            options,
            provider,
            NullLogger.Instance,
            warnings.Add,
            TestContext.Current.CancellationToken);

        Assert.Single(warnings);
        Assert.Contains("OAuthClientId", warnings[0]);
        await provider.DidNotReceiveWithAnyArgs().GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrySignInOnStartupAsync_WhenClientIdWhitespace_TreatedAsMissing()
    {
        var provider = Substitute.For<IGitHubAccessTokenProvider>();
        var options = new CopilotOptions { OAuthClientId = "   " };
        var warnings = new List<string>();

        await App.TrySignInOnStartupAsync(
            options,
            provider,
            NullLogger.Instance,
            warnings.Add,
            TestContext.Current.CancellationToken);

        Assert.Single(warnings);
        await provider.DidNotReceiveWithAnyArgs().GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrySignInOnStartupAsync_WhenClientIdConfigured_KicksOffSignIn()
    {
        var provider = Substitute.For<IGitHubAccessTokenProvider>();
        provider
            .GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("ghu_signed"));
        var options = new CopilotOptions { OAuthClientId = "Iv1.abcdef" };
        var warnings = new List<string>();

        await App.TrySignInOnStartupAsync(
            options,
            provider,
            NullLogger.Instance,
            warnings.Add,
            TestContext.Current.CancellationToken);

        Assert.Empty(warnings);
        await provider.Received(1).GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrySignInOnStartupAsync_WhenProviderThrows_SwallowsExceptionForRetryLater()
    {
        // Eager sign-in is a UX nicety — if it fails (offline, rate-limit, user closes
        // the Device Code dialog) the next Copilot operation will retry. Crashing the
        // process here would defeat the purpose of doing it on startup.
        var provider = Substitute.For<IGitHubAccessTokenProvider>();
        provider
            .GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated transient failure"));
        var options = new CopilotOptions { OAuthClientId = "Iv1.abcdef" };

        await App.TrySignInOnStartupAsync(
            options,
            provider,
            NullLogger.Instance,
            _ => { },
            TestContext.Current.CancellationToken);

        await provider.Received(1).GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrySignInOnStartupAsync_WhenCancelled_SwallowsCancellation()
    {
        var provider = Substitute.For<IGitHubAccessTokenProvider>();
        provider
            .GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        var options = new CopilotOptions { OAuthClientId = "Iv1.abcdef" };

        await App.TrySignInOnStartupAsync(
            options,
            provider,
            NullLogger.Instance,
            _ => { },
            TestContext.Current.CancellationToken);

        await provider.Received(1).GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrySignInOnStartupAsync_NullOptions_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            App.TrySignInOnStartupAsync(
                null!,
                Substitute.For<IGitHubAccessTokenProvider>(),
                NullLogger.Instance,
                _ => { },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TrySignInOnStartupAsync_NullProvider_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            App.TrySignInOnStartupAsync(
                new CopilotOptions { OAuthClientId = "Iv1.x" },
                null!,
                NullLogger.Instance,
                _ => { },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TrySignInOnStartupAsync_NullWarnUser_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            App.TrySignInOnStartupAsync(
                new CopilotOptions { OAuthClientId = "Iv1.x" },
                Substitute.For<IGitHubAccessTokenProvider>(),
                NullLogger.Instance,
                null!,
                TestContext.Current.CancellationToken));
    }
}
