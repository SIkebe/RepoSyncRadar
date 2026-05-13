using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Auth;

public class GitHubAccessTokenProviderTests
{
    [Fact]
    public async Task GetAccessTokenAsync_PrefersEnvironmentOverride()
    {
        // The COPILOT_GITHUB_TOKEN override exists purely so contributors can plug a
        // PAT in while debugging — it must short-circuit before we ever touch the
        // store or the device-flow authenticator.
        var store = Substitute.For<IGitHubTokenStore>();
        var auth = Substitute.For<IGitHubDeviceFlowAuthenticator>();
        var prompt = Substitute.For<IDeviceCodePrompt>();

        using var provider = new GitHubAccessTokenProvider(
            store, auth, prompt, BuildOptions(clientId: null), NullLogger<GitHubAccessTokenProvider>.Instance);

        using var envScope = new EnvOverride(GitHubAccessTokenProvider.EnvironmentOverrideName, "ghp_env_value");

        var token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ghp_env_value", token);
        await store.DidNotReceiveWithAnyArgs().LoadAsync(Arg.Any<CancellationToken>());
        await auth.DidNotReceiveWithAnyArgs().RequestCodeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReusesStoredTokenWithoutDeviceFlow()
    {
        var store = new InMemoryGitHubTokenStore();
        await store.SaveAsync(
            new StoredGitHubToken { AccessToken = "ghu_stored" },
            TestContext.Current.CancellationToken);

        var auth = Substitute.For<IGitHubDeviceFlowAuthenticator>();
        var prompt = Substitute.For<IDeviceCodePrompt>();

        using var provider = new GitHubAccessTokenProvider(
            store, auth, prompt, BuildOptions(clientId: "Iv1.abc"), NullLogger<GitHubAccessTokenProvider>.Instance);
        using var envScope = new EnvOverride(GitHubAccessTokenProvider.EnvironmentOverrideName, null);

        var token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ghu_stored", token);
        await auth.DidNotReceiveWithAnyArgs().RequestCodeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await prompt.DidNotReceiveWithAnyArgs().DisplayAsync(
            Arg.Any<DeviceCodeChallenge>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoStoredToken_RunsDeviceFlowAndPersists()
    {
        var store = new InMemoryGitHubTokenStore();
        var auth = Substitute.For<IGitHubDeviceFlowAuthenticator>();
        var prompt = Substitute.For<IDeviceCodePrompt>();

        var challenge = new DeviceCodeChallenge
        {
            DeviceCode = "deadbeef",
            UserCode = "ABCD-1234",
            VerificationUri = new Uri("https://github.com/login/device"),
            Interval = TimeSpan.FromSeconds(5),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };
        auth.RequestCodeAsync("Iv1.abc", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(challenge);
        auth.PollForTokenAsync("Iv1.abc", challenge, Arg.Any<CancellationToken>())
            .Returns(new StoredGitHubToken { AccessToken = "ghu_new" });

        using var provider = new GitHubAccessTokenProvider(
            store, auth, prompt, BuildOptions(clientId: "Iv1.abc"), NullLogger<GitHubAccessTokenProvider>.Instance);
        using var envScope = new EnvOverride(GitHubAccessTokenProvider.EnvironmentOverrideName, null);

        var token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ghu_new", token);
        var persisted = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ghu_new", persisted!.AccessToken);
        await prompt.Received(1).DisplayAsync(challenge, Arg.Any<CancellationToken>());
        await prompt.Received(1).CloseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoClientIdAndNoStoredToken_Throws()
    {
        var store = new InMemoryGitHubTokenStore();
        var auth = Substitute.For<IGitHubDeviceFlowAuthenticator>();
        var prompt = Substitute.For<IDeviceCodePrompt>();

        using var provider = new GitHubAccessTokenProvider(
            store, auth, prompt, BuildOptions(clientId: null), NullLogger<GitHubAccessTokenProvider>.Instance);
        using var envScope = new EnvOverride(GitHubAccessTokenProvider.EnvironmentOverrideName, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessTokenAsync_DeviceFlowFails_PromptIsAlwaysClosed()
    {
        var store = new InMemoryGitHubTokenStore();
        var auth = Substitute.For<IGitHubDeviceFlowAuthenticator>();
        var prompt = Substitute.For<IDeviceCodePrompt>();

        var challenge = new DeviceCodeChallenge
        {
            DeviceCode = "deadbeef",
            UserCode = "ABCD-1234",
            VerificationUri = new Uri("https://github.com/login/device"),
            Interval = TimeSpan.FromSeconds(5),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };
        auth.RequestCodeAsync("Iv1.abc", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(challenge);
        auth.PollForTokenAsync("Iv1.abc", challenge, Arg.Any<CancellationToken>())
            .ThrowsAsync(new DeviceFlowFailedException("denied"));

        using var provider = new GitHubAccessTokenProvider(
            store, auth, prompt, BuildOptions(clientId: "Iv1.abc"), NullLogger<GitHubAccessTokenProvider>.Instance);
        using var envScope = new EnvOverride(GitHubAccessTokenProvider.EnvironmentOverrideName, null);

        await Assert.ThrowsAsync<DeviceFlowFailedException>(() =>
            provider.GetAccessTokenAsync(TestContext.Current.CancellationToken));

        await prompt.Received(1).CloseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignOutAsync_ClearsCacheAndStore()
    {
        var store = new InMemoryGitHubTokenStore();
        await store.SaveAsync(
            new StoredGitHubToken { AccessToken = "ghu_stored" },
            TestContext.Current.CancellationToken);

        var auth = Substitute.For<IGitHubDeviceFlowAuthenticator>();
        var prompt = Substitute.For<IDeviceCodePrompt>();

        using var provider = new GitHubAccessTokenProvider(
            store, auth, prompt, BuildOptions(clientId: "Iv1.abc"), NullLogger<GitHubAccessTokenProvider>.Instance);
        using var envScope = new EnvOverride(GitHubAccessTokenProvider.EnvironmentOverrideName, null);

        // Prime the in-memory cache.
        _ = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        await provider.SignOutAsync(TestContext.Current.CancellationToken);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    private static IOptions<CopilotOptions> BuildOptions(string? clientId) =>
        Options.Create(new CopilotOptions
        {
            DefaultModel = "gpt-5",
            AllowedUrlHosts = ["docs.github.com"],
            OAuthClientId = clientId,
            OAuthScopes = ["read:user"],
        });

    /// <summary>
    /// RAII helper: sets an environment variable for the scope of one test and
    /// restores the previous value on Dispose. Prevents cross-test leakage when
    /// COPILOT_GITHUB_TOKEN is actually set in the developer's shell.
    /// </summary>
    private sealed class EnvOverride : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvOverride(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
