using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.App.Tests;

public sealed class StartupPreviewPrewarmTests
{
    [Fact]
    public async Task PrewarmPreviewOnStartupAsync_When_Disabled_Skips_Coordinator()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();

        await App.PrewarmPreviewOnStartupAsync(
            new DocsRepositoryOptions(),
            coordinator,
            NullLogger.Instance);

        await coordinator.DidNotReceive().PrewarmAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrewarmPreviewOnStartupAsync_When_Enabled_Calls_Coordinator()
    {
        var coordinator = Substitute.For<IPreviewCoordinator>();

        await App.PrewarmPreviewOnStartupAsync(
            new DocsRepositoryOptions { PrewarmOnStartup = true },
            coordinator,
            NullLogger.Instance);

        await coordinator.Received(1).PrewarmAsync(Arg.Any<CancellationToken>());
    }
}