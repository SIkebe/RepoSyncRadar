using System.IO;
using System.Text;
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
internal static class DocsLiquidContextLoader
{
    private const string ContentDir = "content";
    private const string VariablesSubdir = "variables";
    private const string ReusablesSubdir = "reusables";
    private const string DataDir = "data";

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
        if (variables.Count == 0 && reusables.Count == 0 && pageTitles.Count == 0)
        {
            return DocsLiquidContext.Empty;
        }
        return new DocsLiquidContext(variables, reusables, pageTitles);
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadPageTitlesAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var contentDir = Path.Combine(worktreePath, ContentDir);
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
        }

        return result;
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

    private static void AddAlias(IDictionary<string, string> sink, string key, string rawTitle)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            sink[key] = rawTitle;
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadVariablesAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var variablesDir = Path.Combine(worktreePath, DataDir, VariablesSubdir);
        if (!Directory.Exists(variablesDir))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(variablesDir, "*.yml", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string yaml;
            try
            {
                yaml = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
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

    private static async Task<IReadOnlyDictionary<string, string>> LoadReusablesAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var reusablesDir = Path.Combine(worktreePath, DataDir, ReusablesSubdir);
        if (!Directory.Exists(reusablesDir))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var dirLen = reusablesDir.Length + 1; // +1: trailing separator
        foreach (var file in Directory.EnumerateFiles(reusablesDir, "*.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
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
}
