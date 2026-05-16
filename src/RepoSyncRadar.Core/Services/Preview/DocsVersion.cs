namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// github/docs プラットフォームの「版」識別 (IMPLEMENTATION_PLAN.md §Step 19.9)。
/// 公式 GitHub Docs の右上 Version selector と一致するモデル:
/// <c>fpt</c> = Free, Pro, & Team / <c>ghec</c> = Enterprise Cloud /
/// <c>ghes</c> = Enterprise Server (<see cref="GhesRelease"/> に "3.21" のような
/// release number を持つ)。<see cref="VersionExpressionEvaluator"/> が
/// <c>{% ifversion ... %}</c> の条件式をこの値で評価する。
/// </summary>
public sealed record DocsVersion(DocsPlan Plan, string? GhesRelease = null)
{
    /// <summary>Free, Pro, & Team (github.com).</summary>
    public static DocsVersion Fpt { get; } = new(DocsPlan.Fpt);

    /// <summary>Enterprise Cloud.</summary>
    public static DocsVersion Ghec { get; } = new(DocsPlan.Ghec);

    /// <summary>Enterprise Server の <paramref name="release"/> 版 (例: "3.21")。</summary>
    public static DocsVersion Ghes(string release)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(release);
        return new(DocsPlan.Ghes, release);
    }

    /// <summary>UI dropdown に出る公式ラベル。</summary>
    public string DisplayLabel => Plan switch
    {
        DocsPlan.Fpt => "Free, Pro, & Team",
        DocsPlan.Ghec => "Enterprise Cloud",
        DocsPlan.Ghes when !string.IsNullOrEmpty(GhesRelease) => "Enterprise Server " + GhesRelease,
        DocsPlan.Ghes => "Enterprise Server",
        _ => Plan.ToString(),
    };

    /// <summary>差分検出やルーティングで使う短い識別子 ("fpt" / "ghec" / "ghes-3.21")。</summary>
    public string Slug => Plan switch
    {
        DocsPlan.Fpt => "fpt",
        DocsPlan.Ghec => "ghec",
        DocsPlan.Ghes => "ghes-" + (GhesRelease ?? "latest"),
        _ => Plan.ToString().ToLowerInvariant(),
    };
}

/// <summary>github/docs の plan 種別 (fpt / ghec / ghes)。</summary>
public enum DocsPlan
{
    /// <summary>Free, Pro, & Team (github.com).</summary>
    Fpt,
    /// <summary>GitHub Enterprise Cloud.</summary>
    Ghec,
    /// <summary>GitHub Enterprise Server.</summary>
    Ghes,
}
