using Microsoft.EntityFrameworkCore;
using SNM.Master.Data;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Background;

/// <summary>Shared rollup SQL (docs/DATA.md 3.2): weighted averages, max of maxima, sums of byte counters.</summary>
public static class Rollup
{
    public static string Sql(string target, string source, string tsParam, int width) => $"""
        INSERT INTO {target} (NodeId, Ts, Samples, CpuAvg, CpuMax, MemUsedAvgMb, MemUsedMaxMb, SwapUsedAvgMb,
                              DiskUsedMb, DiskTotalMb, RxBpsAvg, RxBpsMax, TxBpsAvg, TxBpsMax, RxBytes, TxBytes, Load1Avg, Load1Max)
        SELECT NodeId, {tsParam}, SUM(Samples),
               CAST(ROUND(SUM(CpuAvg*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(CpuMax),
               CAST(ROUND(SUM(MemUsedAvgMb*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(MemUsedMaxMb),
               CAST(ROUND(SUM(SwapUsedAvgMb*Samples)*1.0/SUM(Samples)) AS INTEGER),
               CAST(ROUND(SUM(DiskUsedMb*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(DiskTotalMb),
               CAST(ROUND(SUM(RxBpsAvg*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(RxBpsMax),
               CAST(ROUND(SUM(TxBpsAvg*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(TxBpsMax),
               SUM(RxBytes), SUM(TxBytes),
               CAST(ROUND(SUM(Load1Avg*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(Load1Max)
        FROM {source}
        WHERE Ts >= {tsParam} AND Ts < {tsParam} + {width} AND Samples > 0
        GROUP BY NodeId
        ON CONFLICT(NodeId, Ts) DO UPDATE SET
          Samples=excluded.Samples, CpuAvg=excluded.CpuAvg, CpuMax=excluded.CpuMax,
          MemUsedAvgMb=excluded.MemUsedAvgMb, MemUsedMaxMb=excluded.MemUsedMaxMb, SwapUsedAvgMb=excluded.SwapUsedAvgMb,
          DiskUsedMb=excluded.DiskUsedMb, DiskTotalMb=excluded.DiskTotalMb,
          RxBpsAvg=excluded.RxBpsAvg, RxBpsMax=excluded.RxBpsMax, TxBpsAvg=excluded.TxBpsAvg, TxBpsMax=excluded.TxBpsMax,
          RxBytes=excluded.RxBytes, TxBytes=excluded.TxBytes, Load1Avg=excluded.Load1Avg, Load1Max=excluded.Load1Max;
        """;

    public static Task<int> HourAsync(SnmDbContext db, long hourTs, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(Sql("Metrics1h", "Metrics1m", "@ts", 3600), [new Microsoft.Data.Sqlite.SqliteParameter("@ts", hourTs)], ct);

    public static Task<int> DayAsync(SnmDbContext db, long dayTs, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(Sql("Metrics1d", "Metrics1h", "@ts", 86400), [new Microsoft.Data.Sqlite.SqliteParameter("@ts", dayTs)], ct);
}

/// <summary>Hourly at :02 - rolls the previous hour of Metrics1m into Metrics1h; backfills 26 hours at start.</summary>
public sealed class HourlyRollupService(IDbContextFactory<SnmDbContext> dbFactory, DbWriteLock writeLock, NodeRegistry registry, ILogger<HourlyRollupService> logger)
    : SnmBackgroundService(logger, registry)
{
    protected override TimeSpan Period => TimeSpan.FromHours(1);
    protected override TimeSpan InitialDelay(DateTime now) => AlignTo(now, 3600, 120);

    protected override async Task OnStartAsync(CancellationToken ct)
    {
        var lastHour = UnixSeconds(DateTime.UtcNow) / 3600 * 3600 - 3600;
        using var _ = await writeLock.AcquireAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = 0;
        for (var i = 26; i >= 0; i--) rows += await Rollup.HourAsync(db, lastHour - i * 3600, ct);
        Logger.LogInformation("Hourly rollup backfill touched {Rows} rows", rows);
    }

    protected override async Task TickAsync(DateTime now, CancellationToken ct)
    {
        var hourTs = UnixSeconds(now) / 3600 * 3600 - 3600;
        using var _ = await writeLock.AcquireAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await Rollup.HourAsync(db, hourTs, ct);
        // also re-run the current (partial) hour so 7d charts include recent data
        rows += await Rollup.HourAsync(db, hourTs + 3600, ct);
        Logger.LogDebug("Hourly rollup wrote {Rows} rows", rows);
    }
}

/// <summary>Daily at 00:10 UTC - rolls the previous UTC day of Metrics1h into Metrics1d; backfills 8 days at start.</summary>
public sealed class DailyRollupService(IDbContextFactory<SnmDbContext> dbFactory, DbWriteLock writeLock, NodeRegistry registry, ILogger<DailyRollupService> logger)
    : SnmBackgroundService(logger, registry)
{
    protected override TimeSpan Period => TimeSpan.FromHours(1);

    protected override async Task OnStartAsync(CancellationToken ct)
    {
        var today = UnixSeconds(DateTime.UtcNow) / 86400 * 86400;
        using var _ = await writeLock.AcquireAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = 0;
        for (var i = 8; i >= 0; i--) rows += await Rollup.DayAsync(db, today - i * 86400, ct);
        Logger.LogInformation("Daily rollup backfill touched {Rows} rows", rows);
    }

    protected override async Task TickAsync(DateTime now, CancellationToken ct)
    {
        var today = UnixSeconds(now) / 86400 * 86400;
        using var _ = await writeLock.AcquireAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await Rollup.DayAsync(db, today - 86400, ct);
        rows += await Rollup.DayAsync(db, today, ct);
        Logger.LogDebug("Daily rollup wrote {Rows} rows", rows);
    }
}

/// <summary>Hourly at :07 - retention deletes (batched) + daily housekeeping (docs/DATA.md 3.3/3.4).</summary>
public sealed class RetentionService(IDbContextFactory<SnmDbContext> dbFactory, DbWriteLock writeLock, SettingsService settings, NodeRegistry registry, ILogger<RetentionService> logger)
    : SnmBackgroundService(logger, registry)
{
    public const int Metrics1mHours = 25;
    public const int Metrics1hDays = 8;
    public const int Metrics1dDays = 31;

    private DateOnly _lastDaily;

    protected override TimeSpan Period => TimeSpan.FromHours(1);
    protected override TimeSpan InitialDelay(DateTime now) => AlignTo(now, 3600, 420);

    protected override async Task TickAsync(DateTime now, CancellationToken ct)
    {
        var unix = UnixSeconds(now);
        var s = settings.Snapshot;
        using var _ = await writeLock.AcquireAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = 0;
        deleted += await DeleteBatchedAsync(db, "Metrics1m", "Ts", unix - Metrics1mHours * 3600L, ct);
        deleted += await DeleteBatchedAsync(db, "Metrics1h", "Ts", unix - Metrics1hDays * 86400L, ct);
        deleted += await DeleteBatchedAsync(db, "Metrics1d", "Ts", unix - Metrics1dDays * 86400L, ct);
        deleted += await db.AlertEvents.Where(x => x.Status == 2 && x.StartedAt < now.AddDays(-s.RetentionAlertEventDays)).ExecuteDeleteAsync(ct);
        deleted += await db.NotificationDeliveries.Where(x => x.CreatedAt < now.AddDays(-s.RetentionDeliveryDays)).ExecuteDeleteAsync(ct);
        var dailyCutoff = DateOnly.FromDateTime(now).AddDays(-s.RetentionTrafficDailyDays);
        deleted += await db.TrafficDaily.Where(x => x.Date < dailyCutoff).ExecuteDeleteAsync(ct);

        var today = DateOnly.FromDateTime(now);
        if (today != _lastDaily && now.Hour >= 3)
        {
            _lastDaily = today;
            deleted += await db.NodeIps.Where(x => x.LastSeenAt < now.AddHours(-24)).ExecuteDeleteAsync(ct);
            deleted += await db.InstallTokens.Where(x => x.ExpiresAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
            deleted += await db.RefreshTokens.Where(x => (x.ExpiresAt < now.AddDays(-7)) || (x.RevokedAt != null && x.RevokedAt < now.AddDays(-7))).ExecuteDeleteAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
            if (now.DayOfWeek == DayOfWeek.Sunday) await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum(2000);", ct);
        }
        if (deleted > 0) Logger.LogInformation("Retention removed {Rows} rows", deleted);
    }

    private static async Task<int> DeleteBatchedAsync(SnmDbContext db, string table, string column, long cutoff, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
#pragma warning disable EF1003 // table/column names are compile-time constants
            var n = await db.Database.ExecuteSqlRawAsync("DELETE FROM " + table + " WHERE rowid IN (SELECT rowid FROM " + table + " WHERE " + column + " < @cutoff LIMIT 5000)",
                [new Microsoft.Data.Sqlite.SqliteParameter("@cutoff", cutoff)], ct);
#pragma warning restore EF1003
            total += n;
            if (n < 5000) break;
            await Task.Delay(50, ct);
        }
        return total;
    }
}

/// <summary>Every 6 h (first after 10 s): refresh the GeoIP dataset when stale and backfill missing country codes.</summary>
public sealed class GeoIpRefreshService(GeoIpService geoIp, SettingsService settings, IDbContextFactory<SnmDbContext> dbFactory, NodeRegistry registry, ILogger<GeoIpRefreshService> logger)
    : SnmBackgroundService(logger, registry)
{
    protected override TimeSpan Period => TimeSpan.FromHours(6);
    protected override TimeSpan InitialDelay(DateTime now) => TimeSpan.FromSeconds(10);

    protected override async Task TickAsync(DateTime now, CancellationToken ct)
    {
        if (!settings.Snapshot.GeoIpEnabled || !geoIp.Enabled) return;
        if (geoIp.NeedsRefresh())
        {
            try { await geoIp.RefreshAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogWarning("GeoIP refresh failed: {Error}", ex.Message); }
        }
        if (!geoIp.Ready) return;

        var changed = new List<int>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var node in Registry.All)
        {
            var ip = node.RemoteIp.Length > 0 ? node.RemoteIp : node.Meta.LastRemoteIp;
            if (string.IsNullOrEmpty(ip) || node.Meta.CountryCodeAuto is not null) continue;
            if (!System.Net.IPAddress.TryParse(ip, out var addr)) continue;
            var cc = geoIp.Lookup(addr);
            if (cc is null) continue;
            var entity = await db.Nodes.FirstOrDefaultAsync(x => x.Id == node.Id, ct);
            if (entity is null) continue;
            entity.CountryCodeAuto = cc;
            await db.SaveChangesAsync(ct);
            Registry.Upsert(entity);
            changed.Add(node.Id);
        }
        if (changed.Count > 0) Registry.RaiseNodesChanged(changed.ToArray());
    }
}
