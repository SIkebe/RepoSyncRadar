using System.IO;
using System.Text;
using System.Text.Json;

namespace RepoSyncRadar.App.Copilot.Audit;

/// <summary>
/// Production <see cref="IAuditJsonlSink"/>. Writes one JSON line per call into
/// <c>{rootDirectory}/YYYY-MM-DD.jsonl</c>, creating the directory on first use.
/// </summary>
/// <remarks>
/// Concurrency: a single <see cref="SemaphoreSlim"/> serializes appends. The audit traffic is
/// low (one tool invocation at a time per session) so this is intentionally simple.
/// </remarks>
public sealed class FileSystemAuditJsonlSink : IAuditJsonlSink, IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _rootDirectory;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileSystemAuditJsonlSink(string rootDirectory, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(clock);

        _rootDirectory = rootDirectory;
        _clock = clock;
    }

    /// <summary>
    /// Factory for the default path: <c>%LOCALAPPDATA%\RepoSyncRadar\audit</c>.
    /// </summary>
    public static FileSystemAuditJsonlSink CreateDefault(TimeProvider clock)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RepoSyncRadar",
            "audit");
        return new FileSystemAuditJsonlSink(dir, clock);
    }

    public async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var date = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(_rootDirectory, $"{date}.jsonl");
        var line = JsonSerializer.Serialize(record, _jsonOptions) + "\n";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            await File.AppendAllTextAsync(path, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Dispose();
    }
}
