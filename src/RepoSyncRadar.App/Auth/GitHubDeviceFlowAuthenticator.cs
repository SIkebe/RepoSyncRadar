using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RepoSyncRadar.Core.Auth;

namespace RepoSyncRadar.App.Auth;

/// <summary>
/// HttpClient-based implementation of GitHub's OAuth device flow.
/// </summary>
/// <remarks>
/// <para>
/// HttpClient is expected to be configured with <c>BaseAddress = https://github.com/</c>
/// and an <c>Accept: application/json</c> default header via <see cref="HttpClient"/>
/// factory.
/// </para>
/// <para>
/// The implementation deliberately keeps polling timer logic minimal: we honor the
/// initial <c>interval</c> from GitHub, extend by 5 s on every <c>slow_down</c>
/// response per the OAuth Device Authorization Grant RFC §3.5, and stop on
/// <c>expires_at</c>.
/// </para>
/// </remarks>
public sealed partial class GitHubDeviceFlowAuthenticator : IGitHubDeviceFlowAuthenticator
{
    private const string _deviceCodePath = "login/device/code";
    private const string _accessTokenPath = "login/oauth/access_token";
    private const string _deviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly TimeSpan _slowDownIncrement = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubDeviceFlowAuthenticator> _logger;

    public GitHubDeviceFlowAuthenticator(
        HttpClient http,
        TimeProvider timeProvider,
        ILogger<GitHubDeviceFlowAuthenticator> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _timeProvider = timeProvider;
        _logger = logger;

        if (!_http.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
        {
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public async Task<DeviceCodeChallenge> RequestCodeAsync(
        string clientId,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(scopes);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["scope"] = string.Join(' ', scopes),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _deviceCodePath)
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
            throw new DeviceFlowFailedException(string.Create(
                CultureInfo.InvariantCulture,
                $"GitHub device-code request failed with HTTP {(int)response.StatusCode}: {body}"));
        }

        var payload = await response.Content
            .ReadFromJsonAsync<DeviceCodePayload>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DeviceFlowFailedException("GitHub device-code response was empty.");

        if (string.IsNullOrWhiteSpace(payload.DeviceCode) ||
            string.IsNullOrWhiteSpace(payload.UserCode) ||
            string.IsNullOrWhiteSpace(payload.VerificationUri))
        {
            throw new DeviceFlowFailedException("GitHub device-code response was missing required fields.");
        }

        var now = _timeProvider.GetUtcNow();
        return new DeviceCodeChallenge
        {
            DeviceCode = payload.DeviceCode,
            UserCode = payload.UserCode,
            VerificationUri = new Uri(payload.VerificationUri, UriKind.Absolute),
            Interval = TimeSpan.FromSeconds(Math.Max(1, payload.Interval ?? 5)),
            ExpiresAt = now.AddSeconds(Math.Max(1, payload.ExpiresIn ?? 900)),
        };
    }

    public async Task<StoredGitHubToken> PollForTokenAsync(
        string clientId,
        DeviceCodeChallenge challenge,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(challenge);

        var interval = challenge.Interval;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_timeProvider.GetUtcNow() >= challenge.ExpiresAt)
            {
                throw new DeviceFlowFailedException("The device code expired before authorization completed.");
            }

            await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);

            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = clientId,
                ["device_code"] = challenge.DeviceCode,
                ["grant_type"] = _deviceGrantType,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _accessTokenPath)
            {
                Content = new FormUrlEncodedContent(form),
            };

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                throw new DeviceFlowFailedException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"GitHub access-token poll failed with HTTP {(int)response.StatusCode}: {body}"));
            }

            var payload = await response.Content
                .ReadFromJsonAsync<AccessTokenPayload>(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new DeviceFlowFailedException("GitHub access-token response was empty.");

            if (!string.IsNullOrWhiteSpace(payload.Error))
            {
                switch (payload.Error)
                {
                    case "authorization_pending":
                        LogAuthorizationPending(_logger);
                        continue;
                    case "slow_down":
                        interval += _slowDownIncrement;
                        LogSlowDown(_logger, interval.TotalSeconds);
                        continue;
                    case "expired_token":
                        throw new DeviceFlowFailedException("The device code expired before authorization completed.");
                    case "access_denied":
                        throw new DeviceFlowFailedException("Sign-in was denied by the user.");
                    default:
                        throw new DeviceFlowFailedException(string.Create(
                            CultureInfo.InvariantCulture,
                            $"GitHub device flow failed: {payload.Error} ({payload.ErrorDescription})."));
                }
            }

            if (string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                throw new DeviceFlowFailedException("GitHub access-token response was missing access_token.");
            }

            var now = _timeProvider.GetUtcNow();
            var scopes = string.IsNullOrWhiteSpace(payload.Scope)
                ? []
                : payload.Scope.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return new StoredGitHubToken
            {
                AccessToken = payload.AccessToken,
                TokenType = string.IsNullOrWhiteSpace(payload.TokenType) ? "bearer" : payload.TokenType,
                Scopes = scopes,
                RetrievedAt = now,
                ExpiresAt = payload.ExpiresIn is { } seconds && seconds > 0
                    ? now.AddSeconds(seconds)
                    : null,
                RefreshToken = string.IsNullOrWhiteSpace(payload.RefreshToken) ? null : payload.RefreshToken,
            };
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return "<unable to read body>";
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Device-flow: GitHub returned authorization_pending; will retry.")]
    private static partial void LogAuthorizationPending(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Device-flow: GitHub returned slow_down; backing off to {IntervalSeconds:F0}s.")]
    private static partial void LogSlowDown(ILogger logger, double intervalSeconds);

    private sealed record DeviceCodePayload
    {
        [JsonPropertyName("device_code")] public string? DeviceCode { get; init; }
        [JsonPropertyName("user_code")] public string? UserCode { get; init; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; init; }
        [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
        [JsonPropertyName("interval")] public int? Interval { get; init; }
    }

    private sealed record AccessTokenPayload
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("token_type")] public string? TokenType { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
        [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
    }
}
