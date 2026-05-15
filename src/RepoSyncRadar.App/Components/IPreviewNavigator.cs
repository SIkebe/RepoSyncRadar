namespace RepoSyncRadar.App.Components;

/// <summary>
/// Lightweight pub/sub used by <see cref="PreviewActions"/> to ask the WPF host to
/// navigate the right-side WebView2 to a freshly-prepared local preview URL
/// (IMPLEMENTATION_PLAN.md §Step 19.5). Mirrors the <see cref="IReviewBroadcaster"/>
/// pattern: registered as a singleton so the Razor button and the WPF window can
/// communicate without a direct reference.
/// </summary>
public interface IPreviewNavigator
{
    /// <summary>Raised whenever a new preview URL is ready for the host to display.</summary>
    event EventHandler<Uri>? Requested;

    /// <summary>Raised whenever two local preview URLs are ready for visual before/after comparison.</summary>
    event EventHandler<PreviewComparisonRequest>? ComparisonRequested;

    /// <summary>Notifies subscribers that the WebView2 should navigate to <paramref name="url"/>.</summary>
    void Publish(Uri url);

    /// <summary>Notifies subscribers that the WebView2 panes should show a before/after comparison.</summary>
    void PublishComparison(PreviewComparisonRequest request);
}

public sealed record PreviewComparisonRequest(
    Uri BeforeUrl,
    Uri AfterUrl,
    string BeforeLabel,
    string AfterLabel,
    string? FilePath = null,
    int? FileOrdinal = null,
    int? FileCount = null);

/// <inheritdoc cref="IPreviewNavigator" />
public sealed class PreviewNavigator : IPreviewNavigator
{
    public event EventHandler<Uri>? Requested;
    public event EventHandler<PreviewComparisonRequest>? ComparisonRequested;

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
}
