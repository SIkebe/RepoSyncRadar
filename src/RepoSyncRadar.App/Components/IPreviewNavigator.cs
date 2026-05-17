using RepoSyncRadar.Core.Services.Preview;

namespace RepoSyncRadar.App.Components;

/// <summary>
/// Lightweight pub/sub used by <see cref="PreviewActions"/> to ask the WPF host to
/// navigate the right-side WebView2 to a freshly-prepared local preview URL
/// (IMPLEMENTATION_PLAN.md §Step 19.5). Mirrors the <see cref="IReviewBroadcaster"/>
/// pattern: registered as a singleton so the Razor button and the WPF window can
/// communicate without a direct reference.
/// </summary>
/// <remarks>
/// §Step 19.9 — extended bi-directionally:
/// the WPF host (e.g. a Version ComboBox in <c>MainWindow.xaml</c>) can ask the
/// active <see cref="PreviewActions"/> instance to re-render the current preview
/// for a different <see cref="DocsVersion"/> via <see cref="RequestVersionChange"/>.
/// </remarks>
public interface IPreviewNavigator
{
    /// <summary>Raised whenever a new preview URL is ready for the host to display.</summary>
    event EventHandler<Uri>? Requested;

    /// <summary>Raised whenever two local preview URLs are ready for visual before/after comparison.</summary>
    event EventHandler<PreviewComparisonRequest>? ComparisonRequested;

    /// <summary>
    /// Raised when the WPF host wants the currently-active preview component to
    /// re-render against a different docs version (§Step 19.9).
    /// </summary>
    event EventHandler<DocsVersion>? VersionChangeRequested;

    /// <summary>
    /// Raised when the WPF host wants the active preview component to move to
    /// the previous or next previewable file in the current commit.
    /// </summary>
    event EventHandler<PreviewFileNavigationDirection>? FileNavigationRequested;

    /// <summary>Notifies subscribers that the WebView2 should navigate to <paramref name="url"/>.</summary>
    void Publish(Uri url);

    /// <summary>Notifies subscribers that the WebView2 panes should show a before/after comparison.</summary>
    void PublishComparison(PreviewComparisonRequest request);

    /// <summary>
    /// Asks the active preview component to re-render against <paramref name="version"/>.
    /// Triggered by the host when the user picks a new entry in the Version ComboBox.
    /// </summary>
    void RequestVersionChange(DocsVersion version);

    /// <summary>
    /// Asks the active preview component to switch to the adjacent previewable file.
    /// </summary>
    void RequestFileNavigation(PreviewFileNavigationDirection direction);
}

public enum PreviewFileNavigationDirection
{
    Previous = -1,
    Next = 1,
}

public sealed record PreviewComparisonRequest(
    Uri BeforeUrl,
    Uri AfterUrl,
    string BeforeLabel,
    string AfterLabel,
    string? FilePath = null,
    int? FileOrdinal = null,
    int? FileCount = null,
    DocsVersion? CurrentVersion = null,
    IReadOnlyList<DocsVersion>? AffectedVersions = null);

/// <inheritdoc cref="IPreviewNavigator" />
public sealed class PreviewNavigator : IPreviewNavigator
{
    public event EventHandler<Uri>? Requested;
    public event EventHandler<PreviewComparisonRequest>? ComparisonRequested;
    public event EventHandler<DocsVersion>? VersionChangeRequested;
    public event EventHandler<PreviewFileNavigationDirection>? FileNavigationRequested;

    public void Publish(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        Requested?.Invoke(this, url);
    }

    public void PublishComparison(PreviewComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.BeforeUrl);
        ArgumentNullException.ThrowIfNull(request.AfterUrl);
        ComparisonRequested?.Invoke(this, request);
    }

    public void RequestVersionChange(DocsVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        VersionChangeRequested?.Invoke(this, version);
    }

    public void RequestFileNavigation(PreviewFileNavigationDirection direction)
        => FileNavigationRequested?.Invoke(this, direction);
}
