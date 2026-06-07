using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services.Preview;

namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Resolves publishable <c>docs.github.com</c> URLs for changed <c>github/docs</c> files.
/// </summary>
public static class OfficialDocsUrlResolver
{
    public static async Task<IReadOnlyList<string>> LoadAsync(
        RadarDbContext db,
        Commit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(commit);

        var paths = commit.Files
            .Select(static file => file.Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
        {
            return [];
        }

        var mapped = await db.PathUrlMaps
            .AsNoTracking()
            .Where(map => paths.Contains(map.Path))
            .OrderByDescending(map => map.Language == "ja")
            .ThenByDescending(map => map.Version == "fpt")
            .ThenBy(map => map.Path)
            .Select(map => map.Url)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return mapped
            .Concat(BuildFallbackUrls(commit))
            .Select(ToAbsoluteDocsUrl)
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    public static string[] BuildFallbackUrls(Commit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        return commit.Files
            .Select(static file => TryBuildFallbackUrl(file.Path))
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    private static string TryBuildFallbackUrl(string path)
    {
        var route = PreviewPathMapper.Map(path, "en");
        return route is null ? string.Empty : "https://docs.github.com" + route;
    }

    private static string ToAbsoluteDocsUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            return string.Equals(absolute.Host, "docs.github.com", StringComparison.OrdinalIgnoreCase)
                ? NormalizeDocsUri(absolute)
                : string.Empty;
        }
        if (trimmed.StartsWith('/'))
        {
            return Uri.TryCreate(new Uri("https://docs.github.com"), trimmed, out var relative)
                ? NormalizeDocsUri(relative)
                : string.Empty;
        }
        return string.Empty;
    }

    private static string NormalizeDocsUri(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.EndsWith("/index", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/index".Length];
            if (path.Length == 0)
            {
                path = "/";
            }
        }

        var builder = new UriBuilder(uri)
        {
            Path = path,
        };
        return builder.Uri.AbsoluteUri;
    }
}
