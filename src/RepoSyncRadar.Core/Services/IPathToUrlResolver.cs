namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Resolves a <c>github/docs</c> repository path to one or more canonical
/// <c>docs.github.com</c> URLs. This is the DI-friendly counterpart to the pure
/// <see cref="PathToUrlResolver"/> static API — UI components depend on the interface so
/// they can be tested with a substitute. Copilot-facing URL resolution is provided by
/// <c>radar_resolve_url</c>.
/// </summary>
public interface IPathToUrlResolver
{
    /// <summary>
    /// Returns canonical URLs for <paramref name="repoPath"/>. An empty list means the
    /// path is not a publishable article, or no URL is known yet.
    /// </summary>
    /// <param name="repoPath">Repository-relative path such as <c>content/copilot/about-copilot.md</c>.</param>
    /// <param name="language">Preferred UI language. Defaults to <c>en</c>.</param>
    Task<IReadOnlyList<string>> ResolveAsync(
        string repoPath,
        string language = "en",
        CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op default registered until Step 13 wires the real resolver. Returning an empty list
/// keeps the UI rendering even when frontmatter / pagelist sources are unavailable.
/// </summary>
public sealed class NullPathToUrlResolver : IPathToUrlResolver
{
    public Task<IReadOnlyList<string>> ResolveAsync(
        string repoPath,
        string language = "en",
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
}
