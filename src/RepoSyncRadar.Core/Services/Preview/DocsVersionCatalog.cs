namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// 公式 GitHub Docs の Version selector に出る候補一覧
/// (IMPLEMENTATION_PLAN.md §Step 19.9)。
/// <para>
/// 理想は <c>github/docs/data/versions.yml</c> または
/// <c>src/versions/lib/all-versions.ts</c> から動的に取得することだが、
/// 現状は静的リストとしてメンテし、PR レビュー時の見落とし防止に必要な
/// 主要 release を網羅する。ghes はリリース時期に追従して更新する。
/// </para>
/// </summary>
public static class DocsVersionCatalog
{
    /// <summary>
    /// 現時点で公式 Docs が actively publish している ghes リリースの降順リスト。
    /// 必要に応じて更新する (公式の Version selector に出るバージョンだけを保持)。
    /// </summary>
    public static readonly IReadOnlyList<string> GhesReleases = new[]
    {
        "3.21",
        "3.20",
        "3.19",
        "3.18",
        "3.17",
        "3.16",
    };

    /// <summary>UI の Version dropdown に並べる全候補 (fpt → ghec → ghes 降順)。</summary>
    public static IReadOnlyList<DocsVersion> All { get; } = BuildAll();

    /// <summary>未指定時のデフォルト (fpt = github.com)。公式 Docs の既定と一致。</summary>
    public static DocsVersion Default => DocsVersion.Fpt;

    private static List<DocsVersion> BuildAll()
    {
        var list = new List<DocsVersion>(2 + GhesReleases.Count)
        {
            DocsVersion.Fpt,
            DocsVersion.Ghec,
        };
        foreach (var release in GhesReleases)
        {
            list.Add(DocsVersion.Ghes(release));
        }
        return list;
    }

    /// <summary>
    /// <see cref="DocsVersion.Slug"/> から候補を取り出す。未知のスラグでは
    /// <see cref="Default"/> を返し、UI 側の不整合で版が消えないようにする。
    /// </summary>
    public static DocsVersion FromSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Default;
        }
        foreach (var v in All)
        {
            if (string.Equals(v.Slug, slug, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return Default;
    }
}
