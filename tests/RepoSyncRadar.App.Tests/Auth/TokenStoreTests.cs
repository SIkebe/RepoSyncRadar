using RepoSyncRadar.App.Auth;
using Xunit;

namespace RepoSyncRadar.App.Tests.Auth;

public class StoredGitHubTokenTests
{
    [Fact]
    public void IsExpired_NoExpiry_ReturnsFalse()
    {
        var token = new StoredGitHubToken { AccessToken = "ghu_xyz", ExpiresAt = null };
        Assert.False(token.IsExpired);
    }

    [Fact]
    public void IsExpired_ExpiresInTheFuture_ReturnsFalse()
    {
        var token = new StoredGitHubToken
        {
            AccessToken = "ghu_xyz",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        Assert.False(token.IsExpired);
    }

    [Fact]
    public void IsExpired_ExpiresWithinASkewWindow_ReturnsTrue()
    {
        // Tokens that expire in less than a minute are considered expired so we
        // re-auth instead of handing out a token that will fail mid-request.
        var token = new StoredGitHubToken
        {
            AccessToken = "ghu_xyz",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        Assert.True(token.IsExpired);
    }
}

public class InMemoryGitHubTokenStoreTests
{
    [Fact]
    public async Task LoadAsync_BeforeSave_ReturnsNull()
    {
        var store = new InMemoryGitHubTokenStore();
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheToken()
    {
        var store = new InMemoryGitHubTokenStore();
        var token = new StoredGitHubToken
        {
            AccessToken = "ghu_AAA",
            Scopes = ["read:user"],
        };

        await store.SaveAsync(token, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("ghu_AAA", loaded!.AccessToken);
        Assert.Equal(["read:user"], loaded.Scopes);
    }

    [Fact]
    public async Task ClearAsync_RemovesPreviouslySavedToken()
    {
        var store = new InMemoryGitHubTokenStore();
        await store.SaveAsync(
            new StoredGitHubToken { AccessToken = "ghu_AAA" },
            TestContext.Current.CancellationToken);

        await store.ClearAsync(TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(loaded);
    }
}
