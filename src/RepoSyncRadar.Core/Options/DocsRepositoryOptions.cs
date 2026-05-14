using System.Collections.Generic;
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

    /// <summary>
    /// Command arguments. The substring <c>{port}</c> is replaced with the chosen
    /// port. Default is <c>run dev</c> which works for the <c>github/docs</c>
    /// repo — that repo's <c>dev</c> script ignores <c>--port</c> and only honors
    /// the <c>PORT</c> environment variable populated via <see cref="PreviewEnvironment"/>.
    /// </summary>
    public string PreviewArguments { get; set; } = "run dev";

    /// <summary>
    /// Arguments used to install dependencies automatically when a Node-based
    /// preview command is configured and the worktree has no <c>node_modules</c>
    /// directory yet. Empty means the install step is skipped.
    /// </summary>
    public string PreviewInstallArguments { get; set; } = "install";

    /// <summary>
    /// Environment variables to merge on top of the parent process environment
    /// before starting <see cref="PreviewCommand"/>. Values support the <c>{port}</c>
    /// placeholder. Defaults to <c>PORT={port}</c> so the <c>github/docs</c>
    /// server (<c>nodemon src/frame/server.ts</c>) listens on the requested port
    /// rather than its built-in default of 4000.
    /// </summary>
    public Dictionary<string, string> PreviewEnvironment { get; set; } = new(System.StringComparer.Ordinal)
    {
        ["PORT"] = "{port}",
    };

    /// <summary>First port to try when starting the preview server.</summary>
    [Range(1024, 65535)]
    public int PreviewBasePort { get; set; } = 4500;

    /// <summary>
    /// Maximum time the preview server is allowed to take before it must accept
    /// TCP connections on <see cref="PreviewBasePort"/>. <c>github/docs</c>'s
    /// Next.js App Router cold start with full MDX content can comfortably take
    /// 5–10 minutes on Windows ARM64 on the very first run after a
    /// <c>node_modules</c> wipe, so the default is generous. Tune downwards via
    /// <c>appsettings.local.json</c> on a warm machine where you want fast
    /// feedback on real misconfigurations.
    /// </summary>
    [Range(5, 1800)]
    public int PreviewReadyTimeoutSeconds { get; set; } = 600;
}

