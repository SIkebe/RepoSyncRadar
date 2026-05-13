using Octokit;

namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Octokit-backed <see cref="IGitHubUserApi"/>. Builds a one-shot
/// <see cref="GitHubClient"/> per call so the user-info lookup never shares
/// <see cref="IConnection.Credentials"/> mutation with the long-lived
/// <see cref="IGitHubClient"/> singleton used by <c>DocsGitHubClient</c>.
/// </summary>
internal sealed class OctokitGitHubUserApi : IGitHubUserApi
{
    private static readonly ProductHeaderValue Product = new("RepoSyncRadar");

    public async Task<string?> GetCurrentLoginAsync(string accessToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        cancellationToken.ThrowIfCancellationRequested();

        var client = new GitHubClient(Product)
        {
            Credentials = new Credentials(accessToken),
        };

        // Octokit doesn't accept a CancellationToken on User.Current(); honor the
        // token by checking right before/after the network call.
        var user = await client.User.Current().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return user?.Login;
    }
}
