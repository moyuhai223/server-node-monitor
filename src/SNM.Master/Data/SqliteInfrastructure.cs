using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SNM.Master.Data;

/// <summary>Per-connection SQLite PRAGMAs (docs/DATA.md 1.2). journal_mode/auto_vacuum are set once at database creation.</summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-20000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Process-wide write serialization for background jobs (SQLite has a single writer anyway; this avoids
/// busy-retries and keeps large batched writes from interleaving). REST writes are small and rely on busy_timeout.</summary>
public sealed class DbWriteLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
        }
    }
}
