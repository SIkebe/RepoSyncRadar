using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// Shares one <see cref="AppHostFixture"/> across every E2E test class. Launching
/// the WPF host twice in the same test run is fragile because Edge WebView2's
/// per-process state (user-data folder lock, host singleton mutex, debugger
/// port handover) does not always clear instantaneously when the child process
/// tree is killed. Using a collection fixture serializes all E2E tests against
/// a single live app and removes that flakiness without weakening assertions.
/// </summary>
[CollectionDefinition(Name)]
public sealed class E2ETests : ICollectionFixture<AppHostFixture>
{
    public const string Name = "E2E (shared app host)";
}
