namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Executes a single <c>SELECT</c> query against the local radar database. The
/// implementation runs the input through <see cref="SqlGuard"/> first, opens a
/// read-only SQLite connection, and returns the row set without ever touching the
/// schema. See IMPLEMENTATION_PLAN.md §Step 18.
/// </summary>
public interface IRadarQueryRunner
{
    /// <summary>
    /// Runs <paramref name="sql"/> with optional positional <c>?</c> bindings.
    /// Failures (including guard rejection) are surfaced via <see cref="RadarQueryResult.IsValid"/>
    /// rather than thrown so that the caller can render a friendly message to the user.
    /// </summary>
    Task<RadarQueryResult> RunAsync(
        string sql,
        IReadOnlyList<object?>? parameters = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an <see cref="IRadarQueryRunner.RunAsync"/> call.
/// </summary>
public sealed record RadarQueryResult(
    bool IsValid,
    string? Reason,
    string TransformedSql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows);
