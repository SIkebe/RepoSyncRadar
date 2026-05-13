namespace RepoSyncRadar.App.Copilot.Audit;

/// <summary>
/// Append-only JSONL sink for <see cref="AuditRecord"/>. The default production implementation
/// is <see cref="FileSystemAuditJsonlSink"/>; tests substitute their own sink that writes to a
/// per-test temp directory.
/// </summary>
public interface IAuditJsonlSink
{
    /// <summary>Append one JSON line. Must be safe to call concurrently from multiple threads.</summary>
    Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default);
}
