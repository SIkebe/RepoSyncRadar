using System.IO;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// worktree から <see cref="DocsLiquidContext"/> を構築する loader
/// (IMPLEMENTATION_PLAN.md §Step 19.8)。
/// <para>
/// <c>data/variables/**/*.yml</c> を <see cref="YamlStream"/> でパースし、
/// ファイル名を root prefix としてネストキーを再帰展開する
/// (例: <c>product.yml</c> の <c>prodname_copilot_short</c> →
/// <c>"product.prodname_copilot_short"</c>)。
/// </para>
/// <para>
/// <c>data/reusables/**/*.md</c> はディレクトリ区切りをドットに置換した相対パス
/// (例: <c>copilot/about-copilot.md</c> → <c>"copilot.about-copilot"</c>) を
/// キーとして、本文をそのまま値に保持する (Liquid タグを含む生 Markdown)。
/// </para>
/// <para>
/// worktree が存在しない、<c>data/</c> 配下が無い、ファイル I/O が失敗したケースでは
/// <see cref="DocsLiquidContext.Empty"/> 相当の空辞書を返す。プレビューを止めない
/// ことを最優先するため、例外は飲み込んで「未解決の Liquid タグはハイライト span のまま」
/// に縮退させる。
/// </para>
/// </summary>
internal static partial class DocsLiquidContextLoader
{
    private const string _contentDir = "content";
    private const string _variablesSubdir = "variables";
    private const string _reusablesSubdir = "reusables";
    private const string _featuresSubdir = "features";
    private const string _dataDir = "data";

    private static readonly HashSet<string> _nonFeatureIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "and",
        "or",
        "not",
        "fpt",
        "ghec",
        "ghes",
        "ghae",
    };

    [GeneratedRegex(@"\{%-?\s*(?:data|indented_data_reference)\s+reusables\.(?<key>[A-Za-z0-9_.\-/+_]+)(?:\s+[^%]*)?-?%\}", RegexOptions.IgnoreCase)]
    private static partial Regex ReusableReferenceRegex();

    [GeneratedRegex(@"\[AUTOTITLE\]\((?<href>[^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex AutotitleLinkRegex();

    [GeneratedRegex(@"\{%-?\s*for\s+[A-Za-z_][A-Za-z0-9_]*\s+in\s+(?<expr>[A-Za-z0-9_.\-/_]+)\s*-?%\}", RegexOptions.IgnoreCase)]
    private static partial Regex DataSequenceReferenceRegex();

    [GeneratedRegex(@"\b(?:tables\.[A-Za-z0-9_.\-/\[\]""' ]+|enterpriseServerReleases\b)", RegexOptions.IgnoreCase)]
    private static partial Regex DataObjectReferenceRegex();

    [GeneratedRegex(@"\{%-?\s*data\s+variables\.(?<key>[A-Za-z0-9_.\-/+_\[\]]+)\s*-?%\}", RegexOptions.IgnoreCase)]
    private static partial Regex DataVariableReferenceRegex();

    [GeneratedRegex(@"\{\{-?\s*(?:site\.data\.)?variables\.(?<key>[A-Za-z0-9_.\-/_\[\]]+)\s*-?\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex VariableReferenceRegex();

    [GeneratedRegex(@"\{%-?\s*(?:ifversion|elsif)\s+(?<expr>.*?)\s*-?%\}", RegexOptions.Singleline)]
    private static partial Regex IfversionExpressionRegex();

    [GeneratedRegex(@"\b[a-zA-Z_][a-zA-Z0-9_-]*\b")]
    private static partial Regex IdentifierRegex();

    public static async Task<DocsLiquidContext> LoadAsync(
        string? worktreePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return DocsLiquidContext.Empty;
        }

        var variables = await LoadVariablesAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        var reusables = await LoadReusablesAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        var pageTitles = await LoadPageTitlesAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        var dataSequences = await LoadDataSequencesAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        var dataObjects = await LoadDataObjectsAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        var features = await LoadFeaturesAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        if (variables.Count == 0 && reusables.Count == 0 && pageTitles.Count == 0 && dataSequences.Count == 0 && dataObjects.Count == 0 && features.Count == 0)
        {
            return DocsLiquidContext.Empty;
        }
        return new DocsLiquidContext(variables, reusables, pageTitles, dataSequences, features)
        {
            DataObjects = dataObjects,
        };
    }

    public static async Task<DocsLiquidContext> LoadForMarkdownAsync(
        string? worktreePath,
        string repoPath,
        string? markdown,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return DocsLiquidContext.Empty;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        return await LoadForMarkdownAsync(
            new WorktreeDocsFileSource(worktreePath),
            repoPath,
            markdown,
            cancellationToken)
            .ConfigureAwait(false);
        }

    internal static async Task<DocsLiquidContext> LoadForMarkdownAsync(
        IDocsFileSource source,
        string repoPath,
        string? markdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        var reusables = await LoadReferencedReusablesAsync(source, [markdown], cancellationToken).ConfigureAwait(false);
        var liquidSources = new string?[] { markdown }.Concat(reusables.Values).ToArray();
        var variables = await LoadReferencedVariablesAsync(source, liquidSources, cancellationToken).ConfigureAwait(false);
        var expandedLiquidSources = liquidSources.Concat(variables.Values).ToArray();
        var dataSequences = await LoadReferencedDataSequencesAsync(
            source,
                expandedLiquidSources,
                cancellationToken)
            .ConfigureAwait(false);
        var dataObjects = await LoadReferencedDataObjectsAsync(
            source,
            expandedLiquidSources,
            cancellationToken)
            .ConfigureAwait(false);
        var pageTitles = await LoadReferencedPageTitlesAsync(
            source,
                repoPath,
                expandedLiquidSources,
                cancellationToken)
            .ConfigureAwait(false);
        var features = await LoadReferencedFeaturesAsync(
            source,
            expandedLiquidSources,
            cancellationToken)
            .ConfigureAwait(false);
        if (variables.Count == 0 && reusables.Count == 0 && pageTitles.Count == 0 && dataSequences.Count == 0 && dataObjects.Count == 0 && features.Count == 0)
        {
            return DocsLiquidContext.Empty;
        }
        return new DocsLiquidContext(variables, reusables, pageTitles, dataSequences, features)
        {
            DataObjects = dataObjects,
        };
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadPageTitlesAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var contentDir = Path.Combine(worktreePath, _contentDir);
        if (!Directory.Exists(contentDir))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(contentDir, "*.md", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        var root = Path.GetFullPath(worktreePath);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string frontmatter;
            try
            {
                frontmatter = await ReadLeadingFrontmatterAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var rawTitle = ExtractFrontmatterScalar(frontmatter, "title")
                ?? ExtractFrontmatterScalar(frontmatter, "shortTitle");
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(file);
            var repoPath = fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
                ? fullPath[rootWithSeparator.Length..]
                : Path.GetRelativePath(root, fullPath);
            AddPageTitleAliases(result, repoPath, rawTitle);
            foreach (var redirect in ExtractFrontmatterSequence(frontmatter, "redirect_from"))
            {
                AddRouteTitleAliases(result, redirect, rawTitle);
            }
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadReferencedPageTitlesAsync(
        string worktreePath,
        string repoPath,
        IEnumerable<string?> sources,
        CancellationToken cancellationToken)
        => await LoadReferencedPageTitlesAsync(
                new WorktreeDocsFileSource(worktreePath),
                repoPath,
                sources,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IReadOnlyDictionary<string, string>> LoadReferencedPageTitlesAsync(
        IDocsFileSource source,
        string repoPath,
        IEnumerable<string?> sources,
        CancellationToken cancellationToken)
    {
        var normalizedHrefs = sources
            .SelectMany(source => ExtractAutotitleHrefs(source, repoPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedHrefs.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolved = new HashSet<string>(normalizedHrefs, StringComparer.OrdinalIgnoreCase);
        foreach (var href in normalizedHrefs)
        {
            foreach (var candidate in BuildContentPathCandidates(href))
            {
                var content = await source.ReadTextAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (content is null)
                {
                    continue;
                }
                AddPageTitleFromContent(candidate, content, result);
                unresolved.Remove(href);
                break;
            }
        }

        if (unresolved.Count > 0)
        {
            await AddRedirectPageTitlesByScanAsync(source, unresolved, result, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private static IEnumerable<string> ExtractAutotitleHrefs(string? source, string repoPath)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            yield break;
        }

        foreach (Match match in AutotitleLinkRegex().Matches(source))
        {
            var href = match.Groups["href"].Value.Trim();
            var space = href.IndexOfAny([' ', '\t', '\r', '\n']);
            if (space >= 0)
            {
                href = href[..space];
            }
            var normalized = NormalizeAutotitleHref(href, repoPath);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static string? NormalizeAutotitleHref(string href, string repoPath)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var trimmed = href.Trim();
        if (trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var pathWithSuffix = trimmed;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            if (!string.Equals(absoluteUri.Host, "docs.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            pathWithSuffix = absoluteUri.AbsolutePath;
        }

        var suffixStart = pathWithSuffix.IndexOfAny(['?', '#']);
        var path = suffixStart < 0 ? pathWithSuffix : pathWithSuffix[..suffixStart];
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var isRootRelative = path.StartsWith('/');
        var unescaped = Uri.UnescapeDataString(path).Replace('\\', '/');
        var combined = isRootRelative
            ? unescaped.TrimStart('/')
            : CombineRelativePath(GetRepoDirectory(repoPath), unescaped);
        return NormalizeRouteAlias(combined);
    }

    private static string GetRepoDirectory(string repoPath)
    {
        var normalized = repoPath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    private static string CombineRelativePath(string baseDir, string relativePath)
    {
        var segments = new List<string>();
        foreach (var segment in (baseDir + "/" + relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }
            segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static IEnumerable<string> BuildContentPathCandidates(string normalizedHref)
    {
        var trimmed = normalizedHref.Trim('/');
        if (trimmed.Length == 0)
        {
            yield break;
        }

        if (trimmed.StartsWith("content/", StringComparison.Ordinal))
        {
            if (trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                yield return trimmed;
            }
            else
            {
                yield return trimmed + ".md";
                yield return trimmed + "/index.md";
            }
            yield break;
        }

        yield return "content/" + trimmed + ".md";
        yield return "content/" + trimmed + "/index.md";
    }

    private static async Task AddPageTitleFromFileAsync(
        string worktreePath,
        string file,
        IDictionary<string, string> result,
        CancellationToken cancellationToken)
    {
        string frontmatter;
        try
        {
            frontmatter = await ReadLeadingFrontmatterAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var rawTitle = ExtractFrontmatterScalar(frontmatter, "title")
            ?? ExtractFrontmatterScalar(frontmatter, "shortTitle");
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return;
        }

        var root = Path.GetFullPath(worktreePath);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(file);
        var repoPath = fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            ? fullPath[rootWithSeparator.Length..]
            : Path.GetRelativePath(root, fullPath);
        AddPageTitleAliases(result, repoPath, rawTitle);
        foreach (var redirect in ExtractFrontmatterSequence(frontmatter, "redirect_from"))
        {
            AddRouteTitleAliases(result, redirect, rawTitle);
        }
    }

    private static void AddPageTitleFromContent(
        string repoPath,
        string markdown,
        IDictionary<string, string> result)
    {
        var frontmatter = ExtractLeadingFrontmatter(markdown);
        var rawTitle = ExtractFrontmatterScalar(frontmatter, "title")
            ?? ExtractFrontmatterScalar(frontmatter, "shortTitle");
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return;
        }

        AddPageTitleAliases(result, repoPath, rawTitle);
        foreach (var redirect in ExtractFrontmatterSequence(frontmatter, "redirect_from"))
        {
            AddRouteTitleAliases(result, redirect, rawTitle);
        }
    }

    private static async Task AddRedirectPageTitlesByScanAsync(
        string worktreePath,
        HashSet<string> unresolved,
        IDictionary<string, string> result,
        CancellationToken cancellationToken)
        => await AddRedirectPageTitlesByScanAsync(
                new WorktreeDocsFileSource(worktreePath),
                unresolved,
                result,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task AddRedirectPageTitlesByScanAsync(
        IDocsFileSource source,
        HashSet<string> unresolved,
        IDictionary<string, string> result,
        CancellationToken cancellationToken)
    {
        if (unresolved.Count == 0)
        {
            return;
        }

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in BuildRedirectScanDirectories(unresolved))
        {
            foreach (var route in unresolved.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var files = await source.FindFilesContainingAsync(directory, route, ".md", cancellationToken).ConfigureAwait(false);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seenFiles.Add(file))
                    {
                        continue;
                    }

                    var content = await source.ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
                    if (content is null)
                    {
                        continue;
                    }
                    var frontmatter = ExtractLeadingFrontmatter(content);

                    var redirects = ExtractFrontmatterSequence(frontmatter, "redirect_from")
                        .Select(NormalizeRouteAlias)
                        .ToArray();
                    if (!redirects.Any(redirect => unresolved.Contains(redirect)))
                    {
                        continue;
                    }

                    AddPageTitleFromContent(file, content, result);
                    foreach (var redirect in redirects)
                    {
                        unresolved.Remove(redirect);
                    }
                    if (unresolved.Count == 0)
                    {
                        return;
                    }
                }
            }
        }
    }

    private static async Task<string> ReadLeadingFrontmatterAsync(
        string file,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(file);
        var firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (firstLine is null || !string.Equals(firstLine.TrimStart('\uFEFF').TrimEnd('\r'), "---", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var frontmatter = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.Equals(line.TrimEnd('\r'), "---", StringComparison.Ordinal))
            {
                return frontmatter.ToString();
            }
            frontmatter.AppendLine(line);
        }
        return string.Empty;
    }

    private static string ExtractLeadingFrontmatter(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        using var reader = new StringReader(markdown);
        var firstLine = reader.ReadLine();
        if (firstLine is null || !string.Equals(firstLine.TrimStart('\uFEFF').TrimEnd('\r'), "---", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var frontmatter = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line.TrimEnd('\r'), "---", StringComparison.Ordinal))
            {
                return frontmatter.ToString();
            }
            frontmatter.AppendLine(line);
        }
        return string.Empty;
    }

    private static string? ExtractFrontmatterScalar(string frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter))
        {
            return null;
        }
        using var reader = new StringReader(frontmatter);
        string? line;
        var prefix = key + ":";
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var rawValue = line[prefix.Length..].Trim();
            return rawValue.Length == 0 ? null : UnquoteYaml(rawValue);
        }
        return null;
    }

    private static IEnumerable<string> ExtractFrontmatterSequence(string frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter))
        {
            yield break;
        }

        using var reader = new StringReader(frontmatter);
        string? line;
        var prefix = key + ":";
        var inSequence = false;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!inSequence)
            {
                if (line.Length == 0 || char.IsWhiteSpace(line[0]) || !line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                var inlineValue = line[prefix.Length..].Trim();
                if (inlineValue.Length > 0)
                {
                    yield return UnquoteYaml(inlineValue);
                    yield break;
                }
                inSequence = true;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }
            if (!char.IsWhiteSpace(line[0]))
            {
                yield break;
            }

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('-'))
            {
                continue;
            }

            var rawValue = trimmed[1..].Trim();
            if (rawValue.Length > 0)
            {
                yield return UnquoteYaml(rawValue);
            }
        }
    }

    private static string UnquoteYaml(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '\'' && value[^1] == '\'') ||
             (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1];
        }
        return value;
    }

    private static void AddPageTitleAliases(
        IDictionary<string, string> sink,
        string repoPath,
        string rawTitle)
    {
        var normalized = repoPath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        AddAlias(sink, normalized, rawTitle);
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            AddAlias(sink, normalized[..^3], rawTitle);
        }

        if (!normalized.StartsWith("content/", StringComparison.Ordinal))
        {
            return;
        }

        var route = normalized["content/".Length..];
        if (route.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            route = route[..^3];
        }
        if (route.EndsWith("/index", StringComparison.Ordinal))
        {
            route = route[..^"/index".Length];
        }
        route = route.Trim('/');
        if (route.Length == 0)
        {
            return;
        }

        AddAlias(sink, route, rawTitle);
        AddAlias(sink, "/" + route, rawTitle);
        AddAlias(sink, "/en/" + route, rawTitle);
    }

    private static void AddRouteTitleAliases(
        IDictionary<string, string> sink,
        string route,
        string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return;
        }

        var normalized = NormalizeRouteAlias(route);
        if (normalized.Length == 0)
        {
            return;
        }

        AddAlias(sink, normalized, rawTitle);
        AddAlias(sink, "/" + normalized, rawTitle);
        AddAlias(sink, "/en/" + normalized, rawTitle);
    }

    private static string NormalizeRouteAlias(string route)
    {
        var trimmed = route.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "docs.github.com", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = uri.AbsolutePath;
        }

        var suffixStart = trimmed.IndexOfAny(['?', '#']);
        if (suffixStart >= 0)
        {
            trimmed = trimmed[..suffixStart];
        }

        var segments = trimmed.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0)
        {
            return string.Empty;
        }
        if (string.Equals(segments[0], "en", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(0);
        }
        if (segments.Count > 0 && segments[0].Contains('@'))
        {
            segments.RemoveAt(0);
        }
        return string.Join('/', segments);
    }

    private static void AddAlias(IDictionary<string, string> sink, string key, string rawTitle)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            sink[key] = rawTitle;
        }
    }

    private static string[] EnumerateFilesOrEmpty(
        string directory,
        string searchPattern,
        SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateFiles(directory, searchPattern, searchOption).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadVariablesAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var variablesDir = Path.Combine(worktreePath, _dataDir, _variablesSubdir);
        if (!Directory.Exists(variablesDir))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in EnumerateFilesOrEmpty(variablesDir, "*.yml", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string yaml;
            try
            {
                yaml = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var rootPrefix = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(rootPrefix))
            {
                continue;
            }
            TryFlattenYaml(yaml, rootPrefix, result);
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadVariablesAsync(
        IDocsFileSource source,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in await source.EnumerateFilesAsync("data/variables", ".yml", cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var yaml = await source.ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(yaml))
            {
                continue;
            }

            var rootPrefix = Path.GetFileNameWithoutExtension(file.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(rootPrefix))
            {
                continue;
            }
            TryFlattenYaml(yaml, rootPrefix, result);
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadReferencedVariablesAsync(
        IDocsFileSource source,
        IEnumerable<string?> sources,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingSources = new Queue<string?>(sources);
        var loadedRoots = new HashSet<string>(StringComparer.Ordinal);
        while (pendingSources.Count > 0)
        {
            var liquidSource = pendingSources.Dequeue();
            foreach (var root in ExtractVariableRoots(liquidSource))
            {
                if (!loadedRoots.Add(root))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var yaml = await source.ReadTextAsync($"data/variables/{root}.yml", cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    continue;
                }

                var loadedVariables = new Dictionary<string, string>(StringComparer.Ordinal);
                TryFlattenYaml(yaml, root, loadedVariables);
                foreach (var (key, value) in loadedVariables)
                {
                    result[key] = value;
                    pendingSources.Enqueue(value);
                }
            }
        }
        return result;
    }

    private static IEnumerable<string> ExtractVariableRoots(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            yield break;
        }

        foreach (Match match in DataVariableReferenceRegex().Matches(source))
        {
            if (TryGetVariableRoot(match.Groups["key"].Value, out var root))
            {
                yield return root;
            }
        }

        foreach (Match match in VariableReferenceRegex().Matches(source))
        {
            if (TryGetVariableRoot(match.Groups["key"].Value, out var root))
            {
                yield return root;
            }
        }
    }

    private static bool TryGetVariableRoot(string key, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Replace('/', '.');
        var argumentIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (argumentIndex >= 0)
        {
            normalized = normalized[..argumentIndex];
        }
        var bracketIndex = normalized.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex >= 0)
        {
            normalized = normalized[..bracketIndex];
        }
        var dotIndex = normalized.IndexOf('.', StringComparison.Ordinal);
        root = dotIndex >= 0 ? normalized[..dotIndex] : normalized;
        return root.Length > 0;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>> LoadDataSequencesAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var dataDir = Path.Combine(worktreePath, _dataDir);
        if (!Directory.Exists(dataDir))
        {
            return new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal);
        var dirLen = dataDir.Length + 1;
        foreach (var file in EnumerateFilesOrEmpty(dataDir, "*.yml", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length <= dirLen)
            {
                continue;
            }

            var rel = file[dirLen..];
            if (rel.StartsWith(_variablesSubdir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.StartsWith(_variablesSubdir + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            if (rel.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                rel = rel[..^4];
            }
            var key = rel.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
            await AddDataSequenceFromFileAsync(file, key, result, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>> LoadReferencedDataSequencesAsync(
        string worktreePath,
        IEnumerable<string?> sources,
        CancellationToken cancellationToken)
        => await LoadReferencedDataSequencesAsync(
                new WorktreeDocsFileSource(worktreePath),
                sources,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>> LoadReferencedDataSequencesAsync(
        IDocsFileSource source,
        IEnumerable<string?> sources,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var key in sources.SelectMany(ExtractDataSequenceKeys).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = ResolveDataSequenceRepoPath(key);
            var yaml = await source.ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
            if (yaml is null)
            {
                continue;
            }
            AddDataSequenceFromYaml(yaml, key, result);
        }
        return result;
    }

    private static IEnumerable<string> ExtractDataSequenceKeys(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            yield break;
        }

        foreach (Match match in DataSequenceReferenceRegex().Matches(source))
        {
            var key = NormalizeDataSequenceKey(match.Groups["expr"].Value);
            if (key.Length > 0)
            {
                yield return key;
            }
        }
    }

    private static string NormalizeDataSequenceKey(string key)
    {
        var normalized = key.Trim();
        if (normalized.StartsWith("site.data.", StringComparison.Ordinal))
        {
            normalized = normalized["site.data.".Length..];
        }
        if (normalized.StartsWith("data.", StringComparison.Ordinal))
        {
            normalized = normalized["data.".Length..];
        }
        return normalized
            .Replace('/', '.')
            .Replace('\\', '.')
            .Trim('.');
    }

    private static string ResolveDataSequenceFilePath(string worktreePath, string key)
    {
        var relative = key.Replace('.', Path.DirectorySeparatorChar) + ".yml";
        return Path.Combine(worktreePath, _dataDir, relative);
    }

    private static string ResolveDataSequenceRepoPath(string key)
        => _dataDir + "/" + key.Replace('.', '/') + ".yml";

    private static async Task AddDataSequenceFromFileAsync(
        string file,
        string key,
        Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> result,
        CancellationToken cancellationToken)
    {
        string yaml;
        try
        {
            yaml = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var rows = TryParseDataSequenceRows(yaml);
        if (rows.Count > 0)
        {
            result[key] = rows;
        }
    }

    private static void AddDataSequenceFromYaml(
        string yaml,
        string key,
        Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> result)
    {
        var rows = TryParseDataSequenceRows(yaml);
        if (rows.Count > 0)
        {
            result[key] = rows;
        }
    }

    private static List<IReadOnlyDictionary<string, string>> TryParseDataSequenceRows(string yaml)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return rows;
        }

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (Exception)
        {
            return rows;
        }

        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is not YamlSequenceNode sequence)
            {
                continue;
            }

            foreach (var child in sequence.Children)
            {
                if (child is not YamlMappingNode mapping)
                {
                    continue;
                }

                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in mapping.Children)
                {
                    if (entry.Key is YamlScalarNode keyNode && !string.IsNullOrWhiteSpace(keyNode.Value))
                    {
                        row[keyNode.Value!] = ConvertYamlValue(entry.Value);
                    }
                }

                if (row.Count > 0)
                {
                    rows.Add(row);
                }
            }
        }
        return rows;
    }

    private static string ConvertYamlValue(YamlNode node)
        => node switch
        {
            YamlScalarNode scalar => scalar.Value ?? string.Empty,
            YamlSequenceNode sequence => string.Join(", ", sequence.Children.Select(ConvertYamlValue)),
            _ => string.Empty,
        };

    private static async Task<IReadOnlyDictionary<string, DocsLiquidDataValue>> LoadDataObjectsAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var dataDir = Path.Combine(worktreePath, _dataDir);
        var result = new Dictionary<string, DocsLiquidDataValue>(StringComparer.Ordinal);
        if (Directory.Exists(dataDir))
        {
            var dirLen = dataDir.Length + 1;
            foreach (var file in EnumerateFilesOrEmpty(dataDir, "*.yml", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Length <= dirLen)
                {
                    continue;
                }

                var rel = file[dirLen..];
                if (rel.StartsWith(_variablesSubdir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || rel.StartsWith(_variablesSubdir + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }
                if (rel.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    rel = rel[..^4];
                }
                var key = rel.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
                await AddDataObjectFromFileAsync(file, key, result, cancellationToken).ConfigureAwait(false);
            }
        }

        await AddEnterpriseServerReleasesAsync(
            new WorktreeDocsFileSource(worktreePath),
            result,
            cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, DocsLiquidDataValue>> LoadReferencedDataObjectsAsync(
        IDocsFileSource source,
        IEnumerable<string?> sources,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, DocsLiquidDataValue>(StringComparer.Ordinal);
        var keys = sources.SelectMany(ExtractDataObjectKeys).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var key in keys.Where(static key => !string.Equals(key, "enterpriseServerReleases", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AddReferencedDataObjectAsync(source, key, result, cancellationToken).ConfigureAwait(false);
        }

        if (keys.Contains("enterpriseServerReleases", StringComparer.Ordinal))
        {
            await AddEnterpriseServerReleasesAsync(source, result, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private static IEnumerable<string> ExtractDataObjectKeys(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            yield break;
        }

        foreach (Match match in DataObjectReferenceRegex().Matches(source))
        {
            var key = NormalizeDataObjectKey(match.Value);
            if (key.Length > 0)
            {
                yield return key;
            }
        }
    }

    private static string NormalizeDataObjectKey(string key)
    {
        var normalized = key.Trim().TrimEnd('.', ',', ')', ']', '}', '-');
        var pipe = normalized.IndexOf('|', StringComparison.Ordinal);
        if (pipe >= 0)
        {
            normalized = normalized[..pipe].TrimEnd('-', ' ');
        }
        return normalized.Replace('/', '.').Replace('\\', '.').Trim('.');
    }

    private static async Task AddReferencedDataObjectAsync(
        IDocsFileSource source,
        string key,
        Dictionary<string, DocsLiquidDataValue> result,
        CancellationToken cancellationToken)
    {
        var segments = SplitDataObjectPath(key);
        for (var count = segments.Count; count > 0; count--)
        {
            var fileKey = string.Join('.', segments.Take(count));
            var repoPath = _dataDir + "/" + fileKey.Replace('.', '/') + ".yml";
            var yaml = await source.ReadTextAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (yaml is null)
            {
                continue;
            }

            if (!TryParseDataObject(yaml, out var value))
            {
                return;
            }

            result[fileKey] = value;
            var suffix = segments.Skip(count).ToArray();
            if (suffix.Length > 0 && TryGetDataPathValue(value, suffix, out var nested))
            {
                result[string.Join('.', segments)] = nested;
            }
            return;
        }
    }

    private static List<string> SplitDataObjectPath(string key)
    {
        var clean = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (ch == '[')
            {
                break;
            }
            clean.Append(ch);
        }
        return clean.ToString().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static bool TryGetDataPathValue(DocsLiquidDataValue value, IReadOnlyList<string> path, out DocsLiquidDataValue nested)
    {
        nested = value;
        foreach (var segment in path)
        {
            if (nested is not DocsLiquidObjectValue obj || !obj.Properties.TryGetValue(segment, out nested!))
            {
                nested = DocsLiquidDataValue.EmptyString;
                return false;
            }
        }
        return true;
    }

    private static async Task AddDataObjectFromFileAsync(
        string file,
        string key,
        Dictionary<string, DocsLiquidDataValue> result,
        CancellationToken cancellationToken)
    {
        string yaml;
        try
        {
            yaml = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (TryParseDataObject(yaml, out var value))
        {
            result[key] = value;
        }
    }

    private static bool TryParseDataObject(string yaml, out DocsLiquidDataValue value)
    {
        value = DocsLiquidDataValue.EmptyString;
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return false;
        }

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (Exception)
        {
            return false;
        }
        var root = stream.Documents.FirstOrDefault()?.RootNode;
        if (root is null)
        {
            return false;
        }
        value = ConvertYamlDataValue(root);
        return true;
    }

    private static DocsLiquidDataValue ConvertYamlDataValue(YamlNode node)
        => node switch
        {
            YamlScalarNode scalar => new DocsLiquidScalarValue(scalar.Value ?? string.Empty),
            YamlSequenceNode sequence => new DocsLiquidSequenceValue(sequence.Children.Select(ConvertYamlDataValue).ToArray()),
            YamlMappingNode mapping => new DocsLiquidObjectValue(mapping.Children
                .Where(static entry => entry.Key is YamlScalarNode { Value: not null })
                .ToDictionary(
                    static entry => ((YamlScalarNode)entry.Key).Value!,
                    static entry => ConvertYamlDataValue(entry.Value),
                    StringComparer.Ordinal)),
            _ => DocsLiquidDataValue.EmptyString,
        };

    private static async Task AddEnterpriseServerReleasesAsync(
        IDocsFileSource source,
        Dictionary<string, DocsLiquidDataValue> result,
        CancellationToken cancellationToken)
    {
        var datesJson = await source.ReadTextAsync("src/ghes-releases/lib/enterprise-dates.json", cancellationToken).ConfigureAwait(false);
        var releasesTs = await source.ReadTextAsync("src/versions/lib/enterprise-server-releases.ts", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(datesJson) || string.IsNullOrWhiteSpace(releasesTs))
        {
            return;
        }

        var supported = ExtractTsStringArray(releasesTs, "supported");
        var deprecatedFunctional = ExtractTsStringArray(releasesTs, "deprecatedWithFunctionalRedirects");
        var deprecated = deprecatedFunctional.Concat(ExtractDeprecatedTail(releasesTs)).ToArray();
        var dates = ParseEnterpriseDates(datesJson);

        result["enterpriseServerReleases"] = new DocsLiquidObjectValue(new Dictionary<string, DocsLiquidDataValue>(StringComparer.Ordinal)
        {
            ["supported"] = ToScalarSequence(supported),
            ["deprecatedReleasesWithNewFormat"] = ToScalarSequence(deprecated.Where(static version => CompareVersion(version, "2.18") > 0)),
            ["deprecatedReleasesWithLegacyFormat"] = ToScalarSequence(deprecated.Where(static version => CompareVersion(version, "2.18") <= 0)),
            ["deprecatedReleasesOnDeveloperSite"] = ToScalarSequence(deprecated.Where(static version => CompareVersion(version, "2.16") <= 0)),
            ["dates"] = new DocsLiquidObjectValue(dates),
        });
    }

    private static string[] ExtractTsStringArray(string source, string name)
    {
        var match = Regex.Match(source, $"export\\s+const\\s+{Regex.Escape(name)}\\s*=\\s*\\[(?<body>.*?)\\]", RegexOptions.Singleline);
        return match.Success ? ExtractQuotedStrings(match.Groups["body"].Value).ToArray() : [];
    }

    private static IEnumerable<string> ExtractDeprecatedTail(string source)
    {
        var match = Regex.Match(source, "export\\s+const\\s+deprecated\\s*=\\s*\\[(?<body>.*?)\\]", RegexOptions.Singleline);
        if (!match.Success)
        {
            yield break;
        }
        foreach (var value in ExtractQuotedStrings(match.Groups["body"].Value))
        {
            yield return value;
        }
    }

    private static IEnumerable<string> ExtractQuotedStrings(string source)
    {
        foreach (Match match in Regex.Matches(source, "['\"](?<value>[^'\"]+)['\"]"))
        {
            yield return match.Groups["value"].Value;
        }
    }

    private static Dictionary<string, DocsLiquidDataValue> ParseEnterpriseDates(string json)
    {
        var dates = new Dictionary<string, DocsLiquidDataValue>(StringComparer.Ordinal);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return dates;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return dates;
            }

        foreach (var version in document.RootElement.EnumerateObject())
        {
            if (version.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var properties = new Dictionary<string, DocsLiquidDataValue>(StringComparer.Ordinal);
            foreach (var property in version.Value.EnumerateObject())
            {
                properties[property.Name] = new DocsLiquidScalarValue(ReadJsonScalar(property.Value));
            }
            if (properties.TryGetValue("releaseCandidateDate", out var candidate))
            {
                properties["displayCandidateDate"] = candidate;
            }
            if (properties.TryGetValue("generalAvailabilityDate", out var release))
            {
                properties["displayReleaseDate"] = release;
            }
            dates[version.Name] = new DocsLiquidObjectValue(properties);
        }
        }
        return dates;
    }

    private static string ReadJsonScalar(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => element.GetRawText(),
        };

    private static DocsLiquidSequenceValue ToScalarSequence(IEnumerable<string> values)
        => new(values.Select(static value => new DocsLiquidScalarValue(value)).ToArray());

    private static int CompareVersion(string left, string right)
    {
        var leftParts = left.Split('.').Select(ParseVersionPart).ToArray();
        var rightParts = right.Split('.').Select(ParseVersionPart).ToArray();
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < count; i++)
        {
            var l = i < leftParts.Length ? leftParts[i] : 0;
            var r = i < rightParts.Length ? rightParts[i] : 0;
            var comparison = l.CompareTo(r);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return 0;
    }

    private static int ParseVersionPart(string value)
        => int.TryParse(value, out var parsed) ? parsed : 0;

    private static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadFeaturesAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var featuresDir = Path.Combine(worktreePath, _dataDir, _featuresSubdir);
        if (!Directory.Exists(featuresDir))
        {
            return new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var file in EnumerateFilesOrEmpty(featuresDir, "*.yml", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var featureId = Path.GetFileNameWithoutExtension(file);
            await AddFeatureFromFileAsync(file, featureId, result, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadReferencedFeaturesAsync(
        IDocsFileSource source,
        IEnumerable<string?> sources,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var featureId in sources.SelectMany(ExtractFeatureIdentifiers).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var yaml = await source.ReadTextAsync(ResolveFeatureRepoPath(featureId), cancellationToken).ConfigureAwait(false);
            if (yaml is null)
            {
                continue;
            }
            AddFeatureFromYaml(yaml, featureId, result);
        }
        return result;
    }

    private static async Task AddFeatureFromFileAsync(
        string file,
        string featureId,
        Dictionary<string, IReadOnlyDictionary<string, string>> result,
        CancellationToken cancellationToken)
    {
        string yaml;
        try
        {
            yaml = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        AddFeatureFromYaml(yaml, featureId, result);
    }

    private static void AddFeatureFromYaml(
        string yaml,
        string featureId,
        Dictionary<string, IReadOnlyDictionary<string, string>> result)
    {
        var versions = TryParseFeatureVersions(yaml);
        if (versions.Count > 0)
        {
            result[featureId] = versions;
        }
    }

    private static Dictionary<string, string> TryParseFeatureVersions(string yaml)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return versions;
        }

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (Exception)
        {
            return versions;
        }

        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is not YamlMappingNode root)
            {
                continue;
            }

            foreach (var entry in root.Children)
            {
                if (entry.Key is not YamlScalarNode { Value: "versions" }
                    || entry.Value is not YamlMappingNode versionsNode)
                {
                    continue;
                }

                foreach (var versionEntry in versionsNode.Children)
                {
                    if (versionEntry.Key is YamlScalarNode keyNode && !string.IsNullOrWhiteSpace(keyNode.Value))
                    {
                        versions[keyNode.Value!] = ConvertYamlValue(versionEntry.Value);
                    }
                }
            }
        }
        return versions;
    }

    private static IEnumerable<string> ExtractFeatureIdentifiers(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            yield break;
        }

        foreach (Match ifversionMatch in IfversionExpressionRegex().Matches(source))
        {
            foreach (Match identifierMatch in IdentifierRegex().Matches(ifversionMatch.Groups["expr"].Value))
            {
                var value = identifierMatch.Value;
                if (!_nonFeatureIdentifiers.Contains(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static string ResolveFeatureRepoPath(string featureId)
        => _dataDir + "/" + _featuresSubdir + "/" + featureId + ".yml";

    private static async Task<IReadOnlyDictionary<string, string>> LoadReusablesAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var reusablesDir = Path.Combine(worktreePath, _dataDir, _reusablesSubdir);
        if (!Directory.Exists(reusablesDir))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var dirLen = reusablesDir.Length + 1; // +1: trailing separator
        foreach (var file in EnumerateFilesOrEmpty(reusablesDir, "*.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (file.Length <= dirLen)
            {
                continue;
            }
            var rel = file[dirLen..];
            if (rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                rel = rel[..^3];
            }
            // copilot\about-copilot → copilot.about-copilot
            var key = rel.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
            result[key] = content;
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadReferencedReusablesAsync(
        string worktreePath,
        IEnumerable<string?> sources,
        CancellationToken cancellationToken)
        => await LoadReferencedReusablesAsync(
                new WorktreeDocsFileSource(worktreePath),
                sources,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IReadOnlyDictionary<string, string>> LoadReferencedReusablesAsync(
        IDocsFileSource source,
        IEnumerable<string?> sources,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Queue<string>(sources.SelectMany(ExtractReusableKeys));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = NormalizeReusableKey(pending.Dequeue());
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            var file = ResolveReusableRepoPath(key);
            var content = await source.ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                continue;
            }

            result[key] = content;
            foreach (var nested in ExtractReusableKeys(content))
            {
                pending.Enqueue(nested);
            }
        }
        return result;
    }

    private static IEnumerable<string> ExtractReusableKeys(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            yield break;
        }

        foreach (Match match in ReusableReferenceRegex().Matches(source))
        {
            var key = NormalizeReusableKey(match.Groups["key"].Value);
            if (key.Length > 0)
            {
                yield return key;
            }
        }
    }

    internal static bool ContainsReusableReference(string? source, string reusableKey)
    {
        var normalizedKey = NormalizeReusableKey(reusableKey);
        return normalizedKey.Length > 0
            && ExtractReusableKeys(source).Contains(normalizedKey, StringComparer.Ordinal);
    }

    private static string NormalizeReusableKey(string key)
    {
        var normalized = key.Trim();
        var plus = normalized.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            normalized = normalized[..plus];
        }
        return normalized
            .Replace('/', '.')
            .Replace('\\', '.')
            .Trim('.');
    }

    private static string ResolveReusableFilePath(string worktreePath, string key)
    {
        var relative = key.Replace('.', Path.DirectorySeparatorChar) + ".md";
        return Path.Combine(worktreePath, _dataDir, _reusablesSubdir, relative);
    }

    private static string ResolveReusableRepoPath(string key)
        => _dataDir + "/" + _reusablesSubdir + "/" + key.Replace('.', '/') + ".md";

    /// <summary>
    /// YAML を <see cref="YamlStream"/> でパースし、root の <see cref="YamlMappingNode"/>
    /// を <paramref name="rootPrefix"/> 起点で再帰的にドット連結キーに展開する。
    /// シーケンス・スカラー以外のキー (anchors を持つ複合ノード等) はスキップし、
    /// 例外を吐かないようにする (パース失敗時はそのファイル分の variables を諦める)。
    /// </summary>
    internal static void TryFlattenYaml(string yaml, string rootPrefix, IDictionary<string, string> sink)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return;
        }
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (Exception)
        {
            // パース失敗時は黙って諦める (preview は CHANGELOG.md など他のファイルでも動かしたい)
            return;
        }

        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is YamlMappingNode root)
            {
                FlattenMapping(root, rootPrefix, sink);
            }
        }
    }

    private static void FlattenMapping(YamlMappingNode node, string prefix, IDictionary<string, string> sink)
    {
        foreach (var entry in node.Children)
        {
            if (entry.Key is not YamlScalarNode keyNode || keyNode.Value is null)
            {
                continue;
            }
            var compound = string.IsNullOrEmpty(prefix)
                ? keyNode.Value
                : prefix + "." + keyNode.Value;
            switch (entry.Value)
            {
                case YamlScalarNode scalar when scalar.Value is not null:
                    sink[compound] = scalar.Value;
                    break;
                case YamlMappingNode mapping:
                    FlattenMapping(mapping, compound, sink);
                    break;
                // YamlSequenceNode (リスト) は variables では稀。スキップする
                // (必要になったら index 付きキーで展開する)。
                default:
                    break;
            }
        }
    }

    private static string[] BuildRedirectScanDirectories(IEnumerable<string> unresolvedRoutes)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in unresolvedRoutes)
        {
            var segments = route.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var maxPrefixLength = Math.Min(Math.Max(segments.Length - 1, 1), 4);
            for (var take = maxPrefixLength; take >= 1; take--)
            {
                var directory = _contentDir + "/" + string.Join('/', segments.Take(take));
                if (seen.Add(directory))
                {
                    result.Add(directory);
                }
            }
        }
        return result.ToArray();
    }

    private sealed class WorktreeDocsFileSource(string rootPath) : IDocsFileSource
    {
        public async Task<string?> ReadTextAsync(string repoPath, CancellationToken cancellationToken)
        {
            var file = Path.Combine(rootPath, repoPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                return await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public Task<IReadOnlyList<string>> EnumerateFilesAsync(
            string repoDirectory,
            string extension,
            CancellationToken cancellationToken)
        {
            var directory = Path.Combine(rootPath, repoDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            try
            {
                var files = Directory.EnumerateFiles(directory, "*" + extension, SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/'))
                    .ToArray();
                return Task.FromResult<IReadOnlyList<string>>(files);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }
        }

        public async Task<IReadOnlyList<string>> FindFilesContainingAsync(
            string repoDirectory,
            string text,
            string extension,
            CancellationToken cancellationToken)
        {
            var files = await EnumerateFilesAsync(repoDirectory, extension, cancellationToken).ConfigureAwait(false);
            if (files.Count == 0 || string.IsNullOrEmpty(text))
            {
                return Array.Empty<string>();
            }

            var result = new List<string>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = await ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
                if (content?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                {
                    result.Add(file);
                }
            }
            return result;
        }
    }
}

internal interface IDocsFileSource
{
    Task<string?> ReadTextAsync(string repoPath, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> EnumerateFilesAsync(
        string repoDirectory,
        string extension,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> FindFilesContainingAsync(
        string repoDirectory,
        string text,
        string extension,
        CancellationToken cancellationToken);
}
