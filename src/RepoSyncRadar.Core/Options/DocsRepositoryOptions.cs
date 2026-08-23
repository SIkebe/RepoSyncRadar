using System.ComponentModel.DataAnnotations;
using System.IO;

namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Options for the optional local preview pipeline (IMPLEMENTATION_PLAN.md §Step 19).
/// By default, preview clone/cache files are stored under the user's local
/// application data folder so installed builds can preview without manual path setup.
/// When <see cref="BareCloneDir"/> is cleared, the preview pipeline is treated as
/// disabled and every public surface becomes a no-op.
/// </summary>
public sealed class DocsRepositoryOptions
{
    public const string SectionName = "DocsRepository";

    private static readonly string _defaultPreviewRoot = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "RepoSyncRadar",
        "docs-preview");

    /// <summary>Filesystem path of the bare clone (<c>git clone --bare</c>).</summary>
    public string BareCloneDir { get; set; } = Path.Combine(_defaultPreviewRoot, "github-docs.git");

    /// <summary>URL passed to <c>git clone --bare</c>. Required when <see cref="BareCloneDir"/> is set.</summary>
    public string CloneUrl { get; set; } = "https://github.com/github/docs.git";

    /// <summary>Whether the local docs preview pipeline has the required repository settings.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(BareCloneDir)
        && !string.IsNullOrWhiteSpace(CloneUrl);

    /// <summary>Directory used for Markdown preview assets and cleanup of legacy preview worktrees.</summary>
    public string WorktreeRoot { get; set; } = Path.Combine(_defaultPreviewRoot, "worktrees");

    /// <summary>Whether app startup should eagerly create/fetch the bare clone before the first preview action.</summary>
    public bool PrewarmOnStartup { get; set; }

    /// <summary>First port to try when starting the preview server.</summary>
    [Range(1024, 65535)]
    public int PreviewBasePort { get; set; } = 4500;

    /// <summary>
    /// Maximum time a preview operation is allowed to run before the UI cancels
    /// it. First-time repository fetches and Liquid context loading can still
    /// take several minutes on large docs changes, so the default is generous.
    /// </summary>
    [Range(5, 1800)]
    public int PreviewReadyTimeoutSeconds { get; set; } = 600;
}
