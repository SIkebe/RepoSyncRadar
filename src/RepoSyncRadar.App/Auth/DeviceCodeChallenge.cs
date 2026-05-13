namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Result of the <c>POST /login/device/code</c> step of GitHub's OAuth device flow.
/// <see cref="UserCode"/> is displayed to the user; <see cref="DeviceCode"/> is
/// kept secret and only sent during polling.
/// </summary>
public sealed record DeviceCodeChallenge
{
    public required string DeviceCode { get; init; }
    public required string UserCode { get; init; }
    public required Uri VerificationUri { get; init; }
    public required TimeSpan Interval { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
