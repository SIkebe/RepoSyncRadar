using System.ComponentModel;
using Microsoft.Extensions.AI;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
using RepoSyncRadar.Core.Services.Sanitization;

namespace RepoSyncRadar.App.Copilot.Tools;

/// <summary>
/// Registers the read-only <c>radar_*</c> Copilot tools (Step 13). Every tool emitted by
/// <see cref="CreateAll"/> is annotated with <c>skip_permission = true</c> because it has no
/// side effects on the local store, the GitHub repository, or any external system.
/// </summary>
public sealed class RadarTools
{
    private const int DefaultCommitLimit = 50;

    private static readonly string[] BaseVersionIds = ["fpt", "ghec"];

    private readonly IRadarRepository _repo;
    private readonly IDocsGitHubClient _github;
    private readonly IDocsApiClient _docs;
    private readonly TriageScoringProgressTracker _triageProgress;

    public RadarTools(IRadarRepository repo, IDocsGitHubClient github, IDocsApiClient docs)
        : this(repo, github, docs, new TriageScoringProgressTracker())
    {
    }

    public RadarTools(
        IRadarRepository repo,
        IDocsGitHubClient github,
        IDocsApiClient docs,
        TriageScoringProgressTracker triageProgress)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(triageProgress);

        _repo = repo;
        _github = github;
        _docs = docs;
        _triageProgress = triageProgress;
    }

    /// <summary>Returns the four read-only Copilot tools as <see cref="AIFunction"/> instances.</summary>
    public IReadOnlyList<AIFunction> CreateAll()
    {
        return [
            CreateListCommits(),
            CreateGetDiff(),
            CreateResolveUrl(),
            CreateFetchRendered(),
        ];
    }

    internal async Task<CommitsResult> ListCommitsAsync(string? status, int? limit, CancellationToken cancellationToken)
    {
        ReviewStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ReviewStatus>(status, ignoreCase: true, out var s))
        {
            parsedStatus = s;
        }

        var filter = new CommitQueryFilter
        {
            Status = parsedStatus,
            Limit = limit ?? DefaultCommitLimit,
            UnscoredOnly = parsedStatus == ReviewStatus.Unseen,
        };

        var commits = await _repo.QueryCommitsAsync(filter, cancellationToken).ConfigureAwait(false);
        if (parsedStatus == ReviewStatus.Unseen)
        {
            _triageProgress.ReportCommitList(commits.Select(c => c.Sha).ToArray());
        }

        var dtos = new List<CommitDto>(commits.Count);
        foreach (var c in commits)
        {
            var files = c.Files.Select(f => f.Path).ToArray();
            dtos.Add(new CommitDto(
                c.Sha,
                c.PrNumber,
                c.Message,
                c.Author,
                c.AuthoredAt,
                (c.Review?.Status ?? ReviewStatus.Unseen).ToString(),
                files));
        }

        return new CommitsResult(dtos);
    }

    internal async Task<DiffResult> GetDiffAsync(string sha, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        _triageProgress.ReportAnalysisStarted(sha);
        var raw = await _github.GetUnifiedDiffAsync(sha, cancellationToken).ConfigureAwait(false);
        var masked = SecretMasker.Mask(raw);
        var wrapped = UntrustedTextWrapper.Wrap($"diff:{sha}", masked);
        return new DiffResult(sha, wrapped);
    }

    internal async Task<UrlsResult> ResolveUrlAsync(
        string repoPath,
        string frontmatterVersions,
        string language,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentNullException.ThrowIfNull(frontmatterVersions);
        if (string.IsNullOrWhiteSpace(language))
        {
            language = "en";
        }

        var dict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var versionId in BaseVersionIds)
        {
            var pages = await _docs.GetPageListAsync(language, versionId, cancellationToken).ConfigureAwait(false);
            if (pages.Count > 0)
            {
                dict[$"{language}/{versionId}"] = pages;
            }
        }

        var urls = PathToUrlResolver.Resolve(repoPath, frontmatterVersions, dict, language);
        return new UrlsResult(repoPath, urls);
    }

    internal async Task<RenderedHtmlResult> FetchRenderedAsync(string pathname, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathname);

        try
        {
            var body = await _docs.GetArticleBodyAsync(pathname, cancellationToken).ConfigureAwait(false);
            return new RenderedHtmlResult(pathname, body, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RenderedHtmlResult(pathname, BodyHtml: null, Error: ex.Message);
        }
    }

    private AIFunction CreateListCommits()
    {
        return AIFunctionFactory.Create(
            ([Description("Optional review status filter. User-facing labels: 未確認=Unseen, 注目=Adopted, 保留=Later, 見送り候補=Rejected, アーカイブ=Archived. Legacy Seen rows are included in Unseen.")] string? status,
             [Description("Maximum rows to return. Defaults to 50.")] int? limit,
             CancellationToken cancellationToken)
                => ListCommitsAsync(status, limit, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "radar_list_commits",
                Description = "Lists docs commits stored in the local radar.db, optionally filtered by review status.",
                AdditionalProperties = new Dictionary<string, object?> { ["skip_permission"] = true },
            });
    }

    private AIFunction CreateGetDiff()
    {
        return AIFunctionFactory.Create(
            ([Description("Commit SHA (40-char hex) to fetch the unified diff for.")] string sha,
             CancellationToken cancellationToken)
                => GetDiffAsync(sha, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "radar_get_diff",
                Description = "Returns the unified diff for a commit, with secrets masked and wrapped in untrusted-data fences.",
                AdditionalProperties = new Dictionary<string, object?> { ["skip_permission"] = true },
            });
    }

    private AIFunction CreateResolveUrl()
    {
        return AIFunctionFactory.Create(
            ([Description("Repository-relative path (e.g. content/copilot/about-copilot.md).")] string repoPath,
             [Description("Raw text of the frontmatter 'versions:' YAML block.")] string frontmatterVersions,
             [Description("Preferred UI language. Defaults to 'en'.")] string? language,
             CancellationToken cancellationToken)
                => ResolveUrlAsync(repoPath, frontmatterVersions, language ?? "en", cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "radar_resolve_url",
                Description = "Resolves a docs repo file path + frontmatter versions to canonical docs.github.com URLs.",
                AdditionalProperties = new Dictionary<string, object?> { ["skip_permission"] = true },
            });
    }

    private AIFunction CreateFetchRendered()
    {
        return AIFunctionFactory.Create(
            ([Description("Canonical docs pathname (e.g. /en/copilot/about-copilot).")] string pathname,
             CancellationToken cancellationToken)
                => FetchRenderedAsync(pathname, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "radar_fetch_rendered",
                Description = "Fetches the rendered HTML body for a docs.github.com pathname. Returns an error envelope on failure instead of throwing.",
                AdditionalProperties = new Dictionary<string, object?> { ["skip_permission"] = true },
            });
    }
}
