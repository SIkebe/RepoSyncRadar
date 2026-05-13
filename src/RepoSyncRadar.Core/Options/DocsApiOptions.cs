namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Endpoints used to fetch the official rendered docs and the page list.
/// </summary>
public sealed class DocsApiOptions
{
    public const string SectionName = "DocsApi";

    public Uri BaseAddress { get; set; } = new("https://docs.github.com/");

    /// <summary>Default language used when resolving paths to canonical URLs.</summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>Required by the public search API.</summary>
    public string ClientName { get; set; } = "reposyncradar";

    /// <summary>Cache TTL for the per-version page list (seconds).</summary>
    public int PageListCacheSeconds { get; set; } = 86_400;
}
