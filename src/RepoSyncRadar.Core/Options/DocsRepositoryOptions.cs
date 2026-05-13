using System.ComponentModel.DataAnnotations;

namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Options for the optional local preview pipeline (IMPLEMENTATION_PLAN.md §Step 19).
/// All paths/URLs default to <c>string.Empty</c> — when so configured, the preview
/// pipeline is treated as disabled and every public surface becomes a no-op so that
/// the rest of the app can still start.
/// </summary>
public sealed class DocsRepositoryOptions
{
    public const string SectionName = "DocsRepository";

    /// <summary>Filesystem path of the bare clone (<c>git clone --bare</c>).</summary>
    public string BareCloneDir { get; set; } = string.Empty;

    /// <summary>URL passed to <c>git clone --bare</c>. Required when <see cref="BareCloneDir"/> is set.</summary>
    public string CloneUrl { get; set; } = string.Empty;

    /// <summary>Directory under which transient worktrees are added.</summary>
    public string WorktreeRoot { get; set; } = string.Empty;

    /// <summary>Maximum number of worktrees to keep around. Oldest is evicted on overflow.</summary>
    [Range(1, 50)]
    public int MaxWorktrees { get; set; } = 5;

    /// <summary>Command that launches the preview server (e.g. <c>npm</c>).</summary>
    public string PreviewCommand { get; set; } = "npm";

    /// <summary>Arguments. The substring <c>{port}</c> is replaced with the chosen port.</summary>
    public string PreviewArguments { get; set; } = "run dev -- --port {port}";

    /// <summary>First port to try when starting the preview server.</summary>
    [Range(1024, 65535)]
    public int PreviewBasePort { get; set; } = 4500;
}
