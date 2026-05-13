namespace RepoSyncRadar.App.Components;

/// <summary>
/// Lightweight pub/sub for review verdicts. UI components subscribe to refresh state when
/// any review action (Adopt / Reject / Later / Ignore Directory) completes. Implemented
/// as a singleton without a queue, on the assumption that handlers are cheap and the
/// number of subscribers is small (Sidebar + a future CommitList).
/// </summary>
public interface IReviewBroadcaster
{
    /// <summary>Raised after a review action persists successfully.</summary>
    event EventHandler? Reviewed;

    /// <summary>Notifies subscribers that the review state has changed.</summary>
    void Publish();
}

/// <inheritdoc />
public sealed class ReviewBroadcaster : IReviewBroadcaster
{
    public event EventHandler? Reviewed;

    public void Publish() => Reviewed?.Invoke(this, EventArgs.Empty);
}
