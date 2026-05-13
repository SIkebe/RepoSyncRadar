using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.Core.Data;

namespace RepoSyncRadar.Core.Services;

/// <summary>
/// SQLite-backed <see cref="IRadarQueryRunner"/>. Opens a separate read-only
/// connection per query (the radar database is tiny, so connection overhead is
/// negligible compared to the safety win).
/// </summary>
public sealed class SqliteRadarQueryRunner : IRadarQueryRunner
{
    private readonly IDbContextFactory<RadarDbContext> _dbFactory;

    public SqliteRadarQueryRunner(IDbContextFactory<RadarDbContext> dbFactory)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        _dbFactory = dbFactory;
    }

    public async Task<RadarQueryResult> RunAsync(
        string sql,
        IReadOnlyList<object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var guard = SqlGuard.Validate(sql, parameters);
        if (!guard.IsValid)
        {
            return new RadarQueryResult(false, guard.Reason, string.Empty, [], []);
        }

        string baseConnectionString;
        await using (var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            baseConnectionString = ctx.Database.GetConnectionString()
                ?? throw new InvalidOperationException("Radar database connection string is not configured.");
        }
        var readOnly = new SqliteConnectionStringBuilder(baseConnectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        await using var conn = new SqliteConnection(readOnly);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = guard.TransformedSql;
        for (var i = 0; i < guard.Parameters.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.Value = guard.Parameters[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var cols = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            cols[i] = reader.GetName(i);
        }
        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var v = reader.GetValue(i);
                row[i] = v is DBNull ? null : v;
            }
            rows.Add(row);
        }
        return new RadarQueryResult(true, null, guard.TransformedSql, cols, rows);
    }
}
