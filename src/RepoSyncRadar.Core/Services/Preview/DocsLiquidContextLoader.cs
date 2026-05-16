using System.IO;
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
        if (variables.Count == 0 && reusables.Count == 0)
        {
            return DocsLiquidContext.Empty;
        }
        return new DocsLiquidContext(variables, reusables);
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
