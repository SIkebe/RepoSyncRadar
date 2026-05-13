namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Thin wrapper around the public docs.github.com APIs.
/// </summary>
/// <remarks>
/// Used to (a) resolve repository paths to canonical URLs via <c>/api/pagelist</c> and
/// <c>/api/article/meta</c>, and (b) fetch rendered HTML via <c>/api/article/body</c> so the
/// UI can show the same look users would see on the live site.
/// </remarks>
public interface IDocsApiClient
{
    /// <summary>Returns all canonical paths for the given language and product version.</summary>
    Task<IReadOnlyList<string>> GetPageListAsync(
        string language,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the canonical URL for a pathname after redirect normalization.</summary>
    Task<string?> ResolveCanonicalAsync(string pathname, CancellationToken cancellationToken = default);

    /// <summary>Fetches rendered HTML for an article path. The host strips body tags by design.</summary>
    Task<string> GetArticleBodyAsync(string pathname, CancellationToken cancellationToken = default);
}
