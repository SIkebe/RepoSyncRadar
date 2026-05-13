namespace RepoSyncRadar.App.Copilot.Tools;

/// <summary>Result envelope for <c>radar_list_commits</c>.</summary>
public sealed record CommitsResult(IReadOnlyList<CommitDto> Commits);

/// <summary>JSON-serialisable shape of a single commit row exposed to the agent.</summary>
public sealed record CommitDto(
    string Sha,
    int PrNumber,
    string Message,
    string Author,
    DateTime AuthoredAt,
    string Status,
    IReadOnlyList<string> Files);

/// <summary>Result envelope for <c>radar_get_diff</c>. Diff is already masked + fenced.</summary>
public sealed record DiffResult(string Sha, string Diff);

/// <summary>Result envelope for <c>radar_resolve_url</c>.</summary>
public sealed record UrlsResult(string RepoPath, IReadOnlyList<string> Urls);

/// <summary>Result envelope for <c>radar_fetch_rendered</c>. Either <see cref="BodyHtml"/> or <see cref="Error"/> is non-null.</summary>
public sealed record RenderedHtmlResult(string Pathname, string? BodyHtml, string? Error);
