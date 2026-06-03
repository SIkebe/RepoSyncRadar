namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// github/docs リポジトリ固有の Liquid 評価コンテキスト
/// (IMPLEMENTATION_PLAN.md §Step 19.8)。
/// <list type="bullet">
///   <item><see cref="Variables"/>: <c>data/variables/&lt;file&gt;.yml</c> を
///   <c>"&lt;file&gt;.&lt;key&gt;[.&lt;sub&gt;...]"</c> 形式にフラット化した辞書。</item>
///   <item><see cref="Reusables"/>: <c>data/reusables/**/&lt;name&gt;.md</c> を
///   ディレクトリ区切りをドットに変換した相対パス (<c>copilot.about-copilot</c> 等)
///   をキー、本文を値とする辞書。</item>
///   <item><see cref="PageTitles"/>: <c>content/**/*.md</c> の frontmatter
///   <c>title</c> をリンク解決用の repo path / docs path alias で引ける辞書。</item>
///   <item><see cref="DataSequences"/>: <c>data/**/*.yml</c> のうち root が配列の
///   ファイルを <c>tables.copilot.models-and-pricing</c> 形式のキーで引ける辞書。</item>
///   <item><see cref="Features"/>: <c>data/features/*.yml</c> の <c>versions</c>
///   mapping を feature id で引ける辞書。</item>
/// </list>
/// 値は加工せず生のまま保持し、評価時に <see cref="DocsLiquidEvaluator"/> が
/// 再帰展開する。
/// </summary>
public sealed record DocsLiquidContext(
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyDictionary<string, string> Reusables,
    IReadOnlyDictionary<string, string> PageTitles,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> DataSequences,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Features)
{
    public IReadOnlyDictionary<string, DocsLiquidDataValue> DataObjects { get; init; } =
        new Dictionary<string, DocsLiquidDataValue>(StringComparer.Ordinal);

    public DocsLiquidContext(
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string> reusables,
        IReadOnlyDictionary<string, string> pageTitles,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> dataSequences)
        : this(
            variables,
            reusables,
            pageTitles,
            dataSequences,
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal))
    {
    }

    public DocsLiquidContext(
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string> reusables,
        IReadOnlyDictionary<string, string> pageTitles)
        : this(
            variables,
            reusables,
            pageTitles,
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal))
    {
    }

    public DocsLiquidContext(
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string> reusables)
        : this(
            variables,
            reusables,
            new Dictionary<string, string>(StringComparer.Ordinal))
    {
    }

    /// <summary>Liquid 展開対象が一つも見つからなかったときの空コンテキスト。</summary>
    public static DocsLiquidContext Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));
}

public abstract record DocsLiquidDataValue
{
    public static DocsLiquidDataValue EmptyString { get; } = new DocsLiquidScalarValue(string.Empty);
}

public sealed record DocsLiquidScalarValue(string Value) : DocsLiquidDataValue;

public sealed record DocsLiquidSequenceValue(IReadOnlyList<DocsLiquidDataValue> Items) : DocsLiquidDataValue;

public sealed record DocsLiquidObjectValue(IReadOnlyDictionary<string, DocsLiquidDataValue> Properties) : DocsLiquidDataValue;
