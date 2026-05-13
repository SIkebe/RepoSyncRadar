using System.Text.Json.Serialization;

namespace RepoSyncRadar.App.Auth;

/// <summary>
/// User-token record persisted by <see cref="IGitHubTokenStore"/>. Only the access
/// token is required for Copilot SDK authentication; other fields are kept so a
/// future "Signed in as @octocat" affordance can be added without breaking the
/// stored format.
/// </summary>
public sealed record StoredGitHubToken
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "bearer";

    [JsonPropertyName("scopes")]
    public IReadOnlyList<string> Scopes { get; init; } = [];

    [JsonPropertyName("retrieved_at")]
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Absolute expiry timestamp. Null when the token has no documented expiry
    /// (classic OAuth App user tokens are long-lived; only GitHub-App user tokens
    /// expose <c>expires_in</c>).
    /// </summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Refresh token for GitHub-App user-token rotation. Null for classic OAuth Apps.
    /// Currently unused — kept so an upgrade does not require a stored-format change.
    /// </summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    /// <summary>
    /// Conservative expiry check: treat tokens that expire within the next minute as
    /// already expired to avoid races during sign-in / Copilot session creation.
    /// </summary>
    [JsonIgnore]
    public bool IsExpired => ExpiresAt is { } expiresAt &&
        expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1);
}
