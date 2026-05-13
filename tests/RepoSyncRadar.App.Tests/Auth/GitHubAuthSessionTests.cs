using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Auth;

/// <summary>
/// Behaviour tests for <see cref="GitHubAuthSession"/>: the non-destructive auth
/// status surface used by the UI to decide between "未設定 / 未サインイン / サインイン済み"
/// without triggering a device-flow round-trip.
/// </summary>
public class GitHubAuthSessionTests
{
    [Fact]
    public async Task GetStateAsync_ClientIdMissing_ReturnsNotConfigured()
    {
        var store = new InMemoryGitHubTokenStore();
        await store.SaveAsync(
            new StoredGitHubToken { AccessToken = "ghu_x" },
            TestContext.Current.CancellationToken);
        var provider = Substitute.For<IGitHubAccessTokenProvider>();

        var session = new GitHubAuthSession(
            store,
            provider,
            Options.Create(new CopilotOptions { OAuthClientId = string.Empty }),
            NullLogger<GitHubAuthSession>.Instance);

        var state = await session.GetStateAsync(TestContext.Current.CancellationToken);

        // ClientId 未設定だと Device Flow / Sync 自体が不能なので、トークンがあっても
        // 「未設定」として扱う (UI 側で設定誘導を出す)。
        Assert.Equal(GitHubAuthState.NotConfigured, state);
    }

    [Fact]
    public async Task GetStateAsync_ClientIdWhitespace_ReturnsNotConfigured()
    {
        var store = new InMemoryGitHubTokenStore();
        var session = new GitHubAuthSession(
            store,
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = "   " }),
            NullLogger<GitHubAuthSession>.Instance);

        var state = await session.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GitHubAuthState.NotConfigured, state);
    }

    [Fact]
    public async Task GetStateAsync_ClientIdSet_TokenAbsent_ReturnsNotSignedIn()
    {
        var store = new InMemoryGitHubTokenStore();
        var session = new GitHubAuthSession(
            store,
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance);

        var state = await session.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GitHubAuthState.NotSignedIn, state);
    }

    [Fact]
    public async Task GetStateAsync_ClientIdSet_TokenPresent_ReturnsSignedIn()
    {
        var store = new InMemoryGitHubTokenStore();
        await store.SaveAsync(
            new StoredGitHubToken { AccessToken = "ghu_stored" },
            TestContext.Current.CancellationToken);

        var session = new GitHubAuthSession(
            store,
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance);

        var state = await session.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GitHubAuthState.SignedIn, state);
    }

    [Fact]
    public async Task GetStateAsync_ClientIdSet_TokenExpired_ReturnsNotSignedIn()
    {
        // Expired tokens should not be presented as "サインイン済み" — the next sign-in
        // attempt will simply re-run the device flow.
        var store = new InMemoryGitHubTokenStore();
        await store.SaveAsync(
            new StoredGitHubToken
            {
                AccessToken = "ghu_expired",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
            TestContext.Current.CancellationToken);

        var session = new GitHubAuthSession(
            store,
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance);

        var state = await session.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GitHubAuthState.NotSignedIn, state);
    }

    [Fact]
    public async Task SignInAsync_DelegatesToProvider()
    {
        var provider = Substitute.For<IGitHubAccessTokenProvider>();
        provider
            .GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("ghu_new"));

        var session = new GitHubAuthSession(
            new InMemoryGitHubTokenStore(),
            provider,
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance);

        await session.SignInAsync(TestContext.Current.CancellationToken);

        await provider.Received(1).GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignInAsync_WhenNotConfigured_Throws()
    {
        var provider = Substitute.For<IGitHubAccessTokenProvider>();
        var session = new GitHubAuthSession(
            new InMemoryGitHubTokenStore(),
            provider,
            Options.Create(new CopilotOptions { OAuthClientId = string.Empty }),
            NullLogger<GitHubAuthSession>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SignInAsync(TestContext.Current.CancellationToken));

        await provider.DidNotReceiveWithAnyArgs().GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignOutAsync_DelegatesToProvider()
    {
        var provider = Substitute.For<IGitHubAccessTokenProvider>();
        var session = new GitHubAuthSession(
            new InMemoryGitHubTokenStore(),
            provider,
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance);

        await session.SignOutAsync(TestContext.Current.CancellationToken);

        await provider.Received(1).SignOutAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStateAsync_StoreThrows_PropagatesAsNotSignedIn()
    {
        // If DPAPI can't decrypt (profile moved, key reset etc.) we treat that as
        // "user is signed out" so the UI offers re-sign-in instead of a stack trace.
        var store = Substitute.For<IGitHubTokenStore>();
        store
            .LoadAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("dpapi failure"));

        var session = new GitHubAuthSession(
            store,
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance);

        var state = await session.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GitHubAuthState.NotSignedIn, state);
    }

    [Fact]
    public async Task GetCurrentLoginAsync_WhenSignedIn_ReturnsLoginAndCachesPerToken()
    {
        var store = new InMemoryGitHubTokenStore();
        await store.SaveAsync(
            new StoredGitHubToken { AccessToken = "ghu_stored" },
            TestContext.Current.CancellationToken);

        var userApi = Substitute.For<IGitHubUserApi>();
        userApi
            .GetCurrentLoginAsync("ghu_stored", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var session = new GitHubAuthSession(
            store,
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance,
            userApi);

        var first = await session.GetCurrentLoginAsync(TestContext.Current.CancellationToken);
        var second = await session.GetCurrentLoginAsync(TestContext.Current.CancellationToken);

        Assert.Equal("octocat", first);
        Assert.Equal("octocat", second);
        // Second call must reuse the cached login rather than hit GitHub again.
        await userApi.Received(1).GetCurrentLoginAsync("ghu_stored", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCurrentLoginAsync_WhenNotConfigured_ReturnsNullWithoutHittingApi()
    {
        var userApi = Substitute.For<IGitHubUserApi>();
        var session = new GitHubAuthSession(
            new InMemoryGitHubTokenStore(),
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = string.Empty }),
            NullLogger<GitHubAuthSession>.Instance,
            userApi);

        var login = await session.GetCurrentLoginAsync(TestContext.Current.CancellationToken);

        Assert.Null(login);
        await userApi.DidNotReceiveWithAnyArgs().GetCurrentLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCurrentLoginAsync_WhenTokenAbsent_ReturnsNullWithoutHittingApi()
    {
        var userApi = Substitute.For<IGitHubUserApi>();
        var session = new GitHubAuthSession(
            new InMemoryGitHubTokenStore(),
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance,
            userApi);

        var login = await session.GetCurrentLoginAsync(TestContext.Current.CancellationToken);

        Assert.Null(login);
        await userApi.DidNotReceiveWithAnyArgs().GetCurrentLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCurrentLoginAsync_WhenUserApiThrows_ReturnsNull()
    {
        var store = new InMemoryGitHubTokenStore();
        await store.SaveAsync(
            new StoredGitHubToken { AccessToken = "ghu_stored" },
            TestContext.Current.CancellationToken);

        var userApi = Substitute.For<IGitHubUserApi>();
        userApi
            .GetCurrentLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network down"));

        var session = new GitHubAuthSession(
            store,
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance,
            userApi);

        var login = await session.GetCurrentLoginAsync(TestContext.Current.CancellationToken);

        // The AppHeader must remain renderable even when GitHub /user 5xx's.
        Assert.Null(login);
    }

    [Fact]
    public async Task SignOutAsync_ClearsCachedLogin()
    {
        var store = new InMemoryGitHubTokenStore();
        await store.SaveAsync(
            new StoredGitHubToken { AccessToken = "ghu_stored" },
            TestContext.Current.CancellationToken);

        var userApi = Substitute.For<IGitHubUserApi>();
        userApi
            .GetCurrentLoginAsync("ghu_stored", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var session = new GitHubAuthSession(
            store,
            Substitute.For<IGitHubAccessTokenProvider>(),
            Options.Create(new CopilotOptions { OAuthClientId = "Iv1.abc" }),
            NullLogger<GitHubAuthSession>.Instance,
            userApi);

        // Warm the cache, then sign out, then warm again — must hit the API a
        // second time because the cache was cleared.
        _ = await session.GetCurrentLoginAsync(TestContext.Current.CancellationToken);
        await session.SignOutAsync(TestContext.Current.CancellationToken);
        _ = await session.GetCurrentLoginAsync(TestContext.Current.CancellationToken);

        await userApi.Received(2).GetCurrentLoginAsync("ghu_stored", Arg.Any<CancellationToken>());
    }
}
