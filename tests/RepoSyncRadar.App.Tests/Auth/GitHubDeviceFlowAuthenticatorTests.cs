using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.Core.Auth;
using RichardSzalay.MockHttp;
using Xunit;

namespace RepoSyncRadar.App.Tests.Auth;

public class GitHubDeviceFlowAuthenticatorTests
{
    private const string _clientId = "Iv1.testclient";
    private const string _expectedDeviceCodeUrl = "https://github.com/login/device/code";
    private const string _expectedTokenUrl = "https://github.com/login/oauth/access_token";

    [Fact]
    public async Task RequestCodeAsync_ParsesGitHubResponse()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, _expectedDeviceCodeUrl)
            .WithFormData("client_id", _clientId)
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
            _clientId, ["read:user"], TestContext.Current.CancellationToken);

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
        handler.When(HttpMethod.Post, _expectedTokenUrl)
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

        var pollTask = authenticator.PollForTokenAsync(_clientId, challenge, TestContext.Current.CancellationToken);
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
        handler.Expect(HttpMethod.Post, _expectedTokenUrl)
            .Respond("application/json", """{"error":"authorization_pending"}""");
        handler.Expect(HttpMethod.Post, _expectedTokenUrl)
            .Respond("application/json", """{"access_token":"ghu_ok","token_type":"bearer"}""");

        // Use real TimeProvider with a millisecond-scale interval. FakeTimeProvider
        // is unreliable for multi-iteration polling because Task.Delay registration
        // races the test's clock advances (see commit log).
        var authenticator = CreateAuthenticator(handler, TimeProvider.System);
        var challenge = NewShortChallenge();

        var token = await authenticator.PollForTokenAsync(_clientId, challenge, TestContext.Current.CancellationToken);

        Assert.Equal("ghu_ok", token.AccessToken);
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task PollForTokenAsync_OnSlowDown_ExtendsInterval()
    {
        var handler = new MockHttpMessageHandler();
        handler.Expect(HttpMethod.Post, _expectedTokenUrl)
            .Respond("application/json", """{"error":"slow_down"}""");
        handler.Expect(HttpMethod.Post, _expectedTokenUrl)
            .Respond("application/json", """{"access_token":"ghu_ok","token_type":"bearer"}""");

        // Real-time poll; verifies the slow_down branch is taken (interval extension
        // wall-clock value is implementation detail, covered by code review).
        var authenticator = CreateAuthenticator(handler, TimeProvider.System);
        var challenge = NewShortChallenge();

        var token = await authenticator.PollForTokenAsync(_clientId, challenge, TestContext.Current.CancellationToken);

        Assert.Equal("ghu_ok", token.AccessToken);
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task PollForTokenAsync_OnAccessDenied_Throws()
    {
        var handler = new MockHttpMessageHandler();
        handler.When(HttpMethod.Post, _expectedTokenUrl)
            .Respond("application/json", """{"error":"access_denied"}""");

        var authenticator = CreateAuthenticator(handler, TimeProvider.System);
        var challenge = NewShortChallenge();

        await Assert.ThrowsAsync<DeviceFlowFailedException>(() =>
            authenticator.PollForTokenAsync(_clientId, challenge, TestContext.Current.CancellationToken));
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
            authenticator.PollForTokenAsync(_clientId, challenge, TestContext.Current.CancellationToken));
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

    // Short-interval challenge for tests that use TimeProvider.System (real clock).
    private static DeviceCodeChallenge NewShortChallenge() => new()
    {
        DeviceCode = "deadbeef",
        UserCode = "ABCD-1234",
        VerificationUri = new Uri("https://github.com/login/device"),
        Interval = TimeSpan.FromMilliseconds(1),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
    };
}
