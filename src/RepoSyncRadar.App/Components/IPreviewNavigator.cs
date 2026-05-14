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

    /// <summary>Notifies subscribers that the WebView2 should navigate to <paramref name="url"/>.</summary>
    void Publish(Uri url);
}

/// <inheritdoc cref="IPreviewNavigator" />
public sealed class PreviewNavigator : IPreviewNavigator
{
    public event EventHandler<Uri>? Requested;

    public void Publish(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        Requested?.Invoke(this, url);
    }
}
