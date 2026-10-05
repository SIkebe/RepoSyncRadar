using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using RepoSyncRadar.App.Auth;
using Xunit;

namespace RepoSyncRadar.App.Tests.Auth;

public class DpapiGitHubTokenStoreTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ResolveDefaultPath_WithoutOverride_UsesLocalApplicationData(string? configuredPath)
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RepoSyncRadar", "github-token.bin"),
            DpapiGitHubTokenStore.ResolveDefaultPath(configuredPath));
    }

    [Fact]
    public void ResolveDefaultPath_WithOverride_UsesFullConfiguredPath()
    {
        var path = Path.Combine("artifacts", "auth-test", "github-token.bin");
        Assert.Equal(Path.GetFullPath(path), DpapiGitHubTokenStore.ResolveDefaultPath(path));
    }

    [Fact]
    public async Task IsolatedStore_RoundTripsAndClearsToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RepoSyncRadar-Auth-Test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "github-token.bin");
        var store = new DpapiGitHubTokenStore(path, NullLogger<DpapiGitHubTokenStore>.Instance);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            Assert.Null(await store.LoadAsync(ct));
            await store.SaveAsync(new StoredGitHubToken { AccessToken = "ghu_test_placeholder" }, ct);
            Assert.Equal("ghu_test_placeholder", (await store.LoadAsync(ct))?.AccessToken);
            await store.ClearAsync(ct);
            Assert.Null(await store.LoadAsync(ct));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

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
