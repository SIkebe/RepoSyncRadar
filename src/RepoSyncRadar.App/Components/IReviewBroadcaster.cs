namespace RepoSyncRadar.App.Components;

/// <summary>
/// Lightweight pub/sub for inbox-affecting changes. UI components subscribe to refresh state when
/// review actions or triage ingestion change the local commit queue. Implemented as a singleton
/// without a queue, on the assumption that handlers are cheap and the number of subscribers is small.
/// </summary>
public interface IReviewBroadcaster
{
    /// <summary>Raised after the local inbox state changes.</summary>
    event EventHandler? Reviewed;

    /// <summary>Notifies subscribers that the review or inbox state has changed.</summary>
    void Publish();
}

/// <inheritdoc />
public sealed class ReviewBroadcaster : IReviewBroadcaster
{
    public event EventHandler? Reviewed;

    public void Publish() => Reviewed?.Invoke(this, EventArgs.Empty);
}
