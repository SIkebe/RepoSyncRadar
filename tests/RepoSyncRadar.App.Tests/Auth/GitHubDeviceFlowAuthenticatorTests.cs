using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RepoSyncRadar.App.Auth;
using RichardSzalay.MockHttp;
using Xunit;

namespace RepoSyncRadar.App.Tests.Auth;

public class GitHubDeviceFlowAuthenticatorTests
{
    private const string ClientId = "Iv1.testclient";
    private const string ExpectedDeviceCodeUrl = "https://github.com/login/device/code";
    private const string ExpectedTokenUrl = "https://github.com/login/oauth/access_token";

    [Fact]
    public async Task RequestCodeAsync_ParsesGitHubResponse()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, ExpectedDeviceCodeUrl)
            .WithFormData("client_id", ClientId)
            .WithFormData("scope", "read:user")
            .Respond("application/json", """
                {
                  "device_code": "deadbeef",
                  "user_code": "ABCD-1234",
                  "verification_uri": "https://github.com/login/device",
                  "expires_in": 900,
                  "interval": 5
                }
                """);

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var authenticator = CreateAuthenticator(handler, time);

        var challenge = await authenticator.RequestCodeAsync(
            ClientId, ["read:user"], TestContext.Current.CancellationToken);

        Assert.Equal("deadbeef", challenge.DeviceCode);
        Assert.Equal("ABCD-1234", challenge.UserCode);
        Assert.Equal(new Uri("https://github.com/login/device"), challenge.VerificationUri);
        Assert.Equal(TimeSpan.FromSeconds(5), challenge.Interval);
        Assert.Equal(time.GetUtcNow().AddSeconds(900), challenge.ExpiresAt);
    }

    [Fact]
    public async Task PollForTokenAsync_ReturnsAccessTokenWhenGitHubReturnsOne()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, ExpectedTokenUrl)
            .Respond("application/json", """
                {
                  "access_token": "ghu_abc",
                  "token_type": "bearer",
                  "scope": "read:user"
                }
                """);

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var authenticator = CreateAuthenticator(handler, time);
        var challenge = NewChallenge(time);

        var pollTask = authenticator.PollForTokenAsync(ClientId, challenge, TestContext.Current.CancellationToken);
        time.Advance(challenge.Interval);
        var token = await pollTask;

        Assert.Equal("ghu_abc", token.AccessToken);
        Assert.Equal("bearer", token.TokenType);
        Assert.Equal(["read:user"], token.Scopes);
        Assert.Null(token.ExpiresAt);
    }

    [Fact]
    public async Task PollForTokenAsync_OnAuthorizationPending_RetriesUntilSuccess()
    {
        var handler = new MockHttpMessageHandler();
        handler.Expect(HttpMethod.Post, ExpectedTokenUrl)
            .Respond("application/json", """{"error":"authorization_pending"}""");
        handler.Expect(HttpMethod.Post, ExpectedTokenUrl)
            .Respond("application/json", """{"access_token":"ghu_ok","token_type":"bearer"}""");

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var authenticator = CreateAuthenticator(handler, time);
        var challenge = NewChallenge(time);

        var pollTask = authenticator.PollForTokenAsync(ClientId, challenge, TestContext.Current.CancellationToken);
        time.Advance(challenge.Interval);
        // Yield so the awaiter resumes between the two Task.Delay calls before we
        // advance the clock a second time.
        await Task.Yield();
        time.Advance(challenge.Interval);
        var token = await pollTask;

        Assert.Equal("ghu_ok", token.AccessToken);
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task PollForTokenAsync_OnSlowDown_ExtendsInterval()
    {
        var handler = new MockHttpMessageHandler();
        handler.Expect(HttpMethod.Post, ExpectedTokenUrl)
            .Respond("application/json", """{"error":"slow_down"}""");
        handler.Expect(HttpMethod.Post, ExpectedTokenUrl)
            .Respond("application/json", """{"access_token":"ghu_ok","token_type":"bearer"}""");

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var authenticator = CreateAuthenticator(handler, time);
        var challenge = NewChallenge(time);

        var pollTask = authenticator.PollForTokenAsync(ClientId, challenge, TestContext.Current.CancellationToken);
        // first delay = challenge.Interval
        time.Advance(challenge.Interval);
        await Task.Yield();
        // second delay = challenge.Interval + 5s (slow_down extends by 5s per RFC 8628 §3.5)
        time.Advance(challenge.Interval + TimeSpan.FromSeconds(5));
        var token = await pollTask;

        Assert.Equal("ghu_ok", token.AccessToken);
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task PollForTokenAsync_OnAccessDenied_Throws()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, ExpectedTokenUrl)
            .Respond("application/json", """{"error":"access_denied"}""");

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var authenticator = CreateAuthenticator(handler, time);
        var challenge = NewChallenge(time);

        var pollTask = authenticator.PollForTokenAsync(ClientId, challenge, TestContext.Current.CancellationToken);
        time.Advance(challenge.Interval);

        await Assert.ThrowsAsync<DeviceFlowFailedException>(() => pollTask);
    }

    [Fact]
    public async Task PollForTokenAsync_AfterChallengeExpiry_Throws()
    {
        var handler = new MockHttpMessageHandler();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var authenticator = CreateAuthenticator(handler, time);
        var challenge = NewChallenge(time, expiresIn: TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<DeviceFlowFailedException>(() =>
            authenticator.PollForTokenAsync(ClientId, challenge, TestContext.Current.CancellationToken));
    }

    private static GitHubDeviceFlowAuthenticator CreateAuthenticator(MockHttpMessageHandler handler, TimeProvider time)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://github.com/") };
        return new GitHubDeviceFlowAuthenticator(http, time, NullLogger<GitHubDeviceFlowAuthenticator>.Instance);
    }

    private static DeviceCodeChallenge NewChallenge(TimeProvider time, TimeSpan? expiresIn = null) => new()
    {
        DeviceCode = "deadbeef",
        UserCode = "ABCD-1234",
        VerificationUri = new Uri("https://github.com/login/device"),
        Interval = TimeSpan.FromSeconds(5),
        ExpiresAt = time.GetUtcNow().Add(expiresIn ?? TimeSpan.FromMinutes(15)),
    };
}
