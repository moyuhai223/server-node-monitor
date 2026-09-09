using Microsoft.EntityFrameworkCore;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Background;

/// <summary>Every minute (:05): completed 1-minute buckets, traffic state/daily/monthly rows and node state columns, one transaction.</summary>
public sealed class MinuteFlushService(IDbContextFactory<SnmDbContext> dbFactory, DbWriteLock writeLock,
    NodeRegistry registry, ILogger<MinuteFlushService> logger) : SnmBackgroundService(logger, registry)
{
    private int _consecutiveFailures;

    protected override TimeSpan Period => TimeSpan.FromSeconds(60);
    protected override TimeSpan InitialDelay(DateTime now) => AlignTo(now, 60, 5);

    protected override Task TickAsync(DateTime now, CancellationToken ct) => FlushAsync(now, includeOpenBuckets: false, ct);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await FlushAsync(DateTime.UtcNow, includeOpenBuckets: true, CancellationToken.None);
            Logger.LogInformation("Final flush completed");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Final flush failed");
        }
    }

    public async Task FlushAsync(DateTime now, bool includeOpenBuckets, CancellationToken ct)
    {
        var currentBucket = UnixSeconds(now) / 60 * 60;
        var rows = new List<Metric1m>();
        var work = new List<NodeRuntime>();

        foreach (var node in Registry.All)
        {
            while (node.FlushQueue.TryDequeue(out var row)) rows.Add(row);
            lock (node.Sync)
            {
                if (node.Acc is { Samples: > 0 } acc && (includeOpenBuckets || acc.BucketTs < currentBucket))
                {
                    rows.Add(acc.ToRow(node.Id));
                    node.Acc = includeOpenBuckets ? null : node.Acc;
                    if (!includeOpenBuckets) node.Acc = null; // completed bucket; next heartbeat opens a new one
                }
                if (node.Traffic.Dirty || node.StateDirty) work.Add(node);
            }
        }

        if (rows.Count == 0 && work.Count == 0) return;

        try
        {
            using var _ = await writeLock.AcquireAsync(ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            foreach (var row in rows) await UpsertMetricAsync(db, row, ct);

            foreach (var node in work)
            {
                TrafficRuntime t;
                long prevRx, prevTx; DateTime prevAt; DateTime? bootPrev; string? prevConn;
                DateOnly day, pStart, pEnd; long dayRx, dayTx, perRx, perTx;
                ClosedPeriod[] closed; (DateOnly, long, long)[] pastDays;
                bool trafficDirty, stateDirty;
                DateTime? lastSeen, statusChanged; int status; string remoteIp;

                lock (node.Sync)
                {
                    t = node.Traffic;
                    trafficDirty = t.Dirty; stateDirty = node.StateDirty;
                    prevRx = t.PrevRx; prevTx = t.PrevTx; prevAt = t.PrevAt; bootPrev = t.BootTimeAtPrev; prevConn = t.PrevConnectionId;
                    day = t.Day; dayRx = t.DayRx; dayTx = t.DayTx; pStart = t.PeriodStart; pEnd = t.PeriodEnd; perRx = t.PerRx; perTx = t.PerTx;
                    closed = t.PendingClosed.ToArray(); pastDays = t.PendingDays.ToArray();
                    t.PendingClosed.Clear(); t.PendingDays.Clear();
                    t.Dirty = false; node.StateDirty = false;
                    lastSeen = node.LastSeenAt; statusChanged = node.StatusChangedAt; status = node.Status; remoteIp = node.RemoteIp;
                }

                var meta = node.Meta;
                if (trafficDirty)
                {
                    var state = await db.TrafficStates.FirstOrDefaultAsync(x => x.NodeId == node.Id, ct);
                    if (state is null) { state = new TrafficState { NodeId = node.Id }; db.TrafficStates.Add(state); }
                    state.PrevRx = prevRx; state.PrevTx = prevTx; state.PrevAtUtc = prevAt == DateTime.MinValue ? null : prevAt;
                    state.BootTimeAtPrevUtc = bootPrev; state.PrevConnectionId = prevConn; state.UpdatedAt = now;

                    foreach (var (date, rx, txb) in pastDays) await UpsertDailyAsync(db, node.Id, date, rx, txb, now, ct);
                    if (day != default) await UpsertDailyAsync(db, node.Id, day, dayRx, dayTx, now, ct);

                    foreach (var c in closed)
                        await UpsertMonthlyAsync(db, node.Id, c.PeriodStart, c.PeriodEnd, c.Rx, c.Tx, meta, closedFlag: true, now, ct);
                    if (pStart != default)
                        await UpsertMonthlyAsync(db, node.Id, pStart, pEnd, perRx, perTx, meta, closedFlag: false, now, ct);
                }

                if (stateDirty)
                {
                    await db.Nodes.Where(x => x.Id == node.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.LastSeenAt, lastSeen)
                        .SetProperty(x => x.Status, status)
                        .SetProperty(x => x.StatusChangedAt, statusChanged)
                        .SetProperty(x => x.LastRemoteIp, remoteIp.Length > 0 ? remoteIp : null), ct);
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _consecutiveFailures = 0;
            Logger.LogDebug("Flushed {Rows} metric rows and {Nodes} node states", rows.Count, work.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Put the metric rows back so the next tick retries them; traffic/state dirty flags are re-raised.
            foreach (var row in rows) Registry.Get(row.NodeId)?.FlushQueue.Enqueue(row);
            foreach (var node in work) { node.Traffic.Dirty = true; node.StateDirty = true; }
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3) Logger.LogError(ex, "Minute flush failed {Count} times in a row", _consecutiveFailures);
            else Logger.LogWarning(ex, "Minute flush failed; will retry");
        }
    }

    private static Task UpsertMetricAsync(SnmDbContext db, Metric1m r, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO Metrics1m (NodeId, Ts, Samples, CpuAvg, CpuMax, MemUsedAvgMb, MemUsedMaxMb, SwapUsedAvgMb, DiskUsedMb, DiskTotalMb,
                               RxBpsAvg, RxBpsMax, TxBpsAvg, TxBpsMax, RxBytes, TxBytes, Load1Avg, Load1Max)
        VALUES ({r.NodeId}, {r.Ts}, {r.Samples}, {r.CpuAvg}, {r.CpuMax}, {r.MemUsedAvgMb}, {r.MemUsedMaxMb}, {r.SwapUsedAvgMb}, {r.DiskUsedMb}, {r.DiskTotalMb},
                {r.RxBpsAvg}, {r.RxBpsMax}, {r.TxBpsAvg}, {r.TxBpsMax}, {r.RxBytes}, {r.TxBytes}, {r.Load1Avg}, {r.Load1Max})
        ON CONFLICT(NodeId, Ts) DO UPDATE SET
          Samples = Samples + excluded.Samples,
          CpuAvg = (CpuAvg * Samples + excluded.CpuAvg * excluded.Samples) / (Samples + excluded.Samples),
          CpuMax = MAX(CpuMax, excluded.CpuMax),
          MemUsedAvgMb = (MemUsedAvgMb * Samples + excluded.MemUsedAvgMb * excluded.Samples) / (Samples + excluded.Samples),
          MemUsedMaxMb = MAX(MemUsedMaxMb, excluded.MemUsedMaxMb),
          SwapUsedAvgMb = (SwapUsedAvgMb * Samples + excluded.SwapUsedAvgMb * excluded.Samples) / (Samples + excluded.Samples),
          DiskUsedMb = excluded.DiskUsedMb, DiskTotalMb = excluded.DiskTotalMb,
          RxBpsAvg = (RxBpsAvg * Samples + excluded.RxBpsAvg * excluded.Samples) / (Samples + excluded.Samples),
          RxBpsMax = MAX(RxBpsMax, excluded.RxBpsMax),
          TxBpsAvg = (TxBpsAvg * Samples + excluded.TxBpsAvg * excluded.Samples) / (Samples + excluded.Samples),
          TxBpsMax = MAX(TxBpsMax, excluded.TxBpsMax),
          RxBytes = RxBytes + excluded.RxBytes, TxBytes = TxBytes + excluded.TxBytes,
          Load1Avg = (Load1Avg * Samples + excluded.Load1Avg * excluded.Samples) / (Samples + excluded.Samples),
          Load1Max = MAX(Load1Max, excluded.Load1Max);
        """, ct);

    private static async Task UpsertDailyAsync(SnmDbContext db, int nodeId, DateOnly date, long rx, long tx, DateTime now, CancellationToken ct)
    {
        var row = await db.TrafficDaily.FirstOrDefaultAsync(x => x.NodeId == nodeId && x.Date == date, ct);
        if (row is null) db.TrafficDaily.Add(new TrafficDaily { NodeId = nodeId, Date = date, RxBytes = rx, TxBytes = tx, UpdatedAt = now });
        else { row.RxBytes = rx; row.TxBytes = tx; row.UpdatedAt = now; }
    }

    private static async Task UpsertMonthlyAsync(SnmDbContext db, int nodeId, DateOnly start, DateOnly end, long rx, long tx, Node meta, bool closedFlag, DateTime now, CancellationToken ct)
    {
        var row = await db.TrafficMonthly.FirstOrDefaultAsync(x => x.NodeId == nodeId && x.PeriodStart == start, ct);
        if (row is null)
        {
            row = new TrafficMonthly { NodeId = nodeId, PeriodStart = start };
            db.TrafficMonthly.Add(row);
        }
        row.PeriodEnd = end; row.RxBytes = rx; row.TxBytes = tx;
        row.BilledBytes = BillingPeriod.Billed(rx, tx, meta.TrafficCountMode);
        row.LimitBytes = meta.TrafficLimitBytes; row.CountMode = meta.TrafficCountMode; row.ResetDay = meta.TrafficResetDay;
        row.Closed = closedFlag; row.UpdatedAt = now;
    }
}
