namespace RepoSyncRadar.Core.Models;

/// <summary>
/// Filter passed to <see cref="Data.IRadarRepository.QueryCommitsAsync"/> by the UI.
/// All members are optional; an empty filter returns every persisted commit ordered by
/// <see cref="Commit.AuthoredAt"/> descending.
/// </summary>
public sealed record CommitQueryFilter
{
    /// <summary>
    /// When supplied, only commits whose <see cref="Review.Status"/> equals this value are
    /// returned. A commit without a <see cref="Review"/> row is treated as
    /// <see cref="ReviewStatus.Unseen"/>. Legacy <see cref="ReviewStatus.Seen"/> rows are
    /// also returned by the <see cref="ReviewStatus.Unseen"/> filter.
    /// </summary>
    public ReviewStatus? Status { get; init; }

    /// <summary>
    /// Maximum number of commits to return. When null, no limit is applied.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// When supplied, only commits whose SHA, PR number, or message contains this value are
    /// returned. Short hashes are accepted; text matching is performed after trimming and
    /// lower-casing.
    /// </summary>
    public string? ShaQuery { get; init; }

    /// <summary>
    /// When true, only commits that do not yet have a <see cref="Scoring"/> row are returned.
    /// </summary>
    public bool UnscoredOnly { get; init; }

    /// <summary>
    /// When true, commits are ordered by <see cref="Commit.AuthoredAt"/> ascending instead of
    /// the default descending order. Use this for FIFO processing queues without changing the UI order.
    /// </summary>
    public bool OldestFirst { get; init; }
}
