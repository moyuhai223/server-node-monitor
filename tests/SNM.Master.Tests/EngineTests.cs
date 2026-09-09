using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Master.Background;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Runtime;

namespace SNM.Master.Tests;

public class BillingPeriodTests
{
    [Theory]
    [InlineData("2026-09-15", 1, "2026-09-01", "2026-10-01")]
    [InlineData("2026-09-01", 1, "2026-09-01", "2026-10-01")]
    [InlineData("2026-08-31", 1, "2026-08-01", "2026-09-01")]
    [InlineData("2026-02-10", 31, "2026-01-31", "2026-02-28")]   // Feb clamps 31 -> 28
    [InlineData("2026-02-28", 31, "2026-02-28", "2026-03-31")]
    [InlineData("2024-02-29", 30, "2024-02-29", "2024-03-30")]   // leap year clamp 30 -> 29
    [InlineData("2026-03-05", 15, "2026-02-15", "2026-03-15")]
    public void PeriodBoundariesClampToMonthEnd(string today, int resetDay, string start, string end)
    {
        var t = DateOnly.Parse(today);
        var s = BillingPeriod.PeriodStart(t, resetDay);
        Assert.Equal(DateOnly.Parse(start), s);
        Assert.Equal(DateOnly.Parse(end), BillingPeriod.PeriodEnd(s, resetDay));
    }

    [Fact]
    public void BilledFollowsCountMode()
    {
        Assert.Equal(30, BillingPeriod.Billed(10, 20, TrafficCountMode.RxPlusTx));
        Assert.Equal(20, BillingPeriod.Billed(10, 20, TrafficCountMode.TxOnly));
        Assert.Equal(10, BillingPeriod.Billed(10, 20, TrafficCountMode.RxOnly));
        Assert.Equal(20, BillingPeriod.Billed(10, 20, TrafficCountMode.MaxOfRxTx));
    }

    [Fact]
    public void LocalTodayHonoursTimeZone()
    {
        var tz = BillingPeriod.ResolveTimeZone("Asia/Shanghai", "UTC");
        var utc = new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc);   // 04:00 next day in Shanghai
        Assert.Equal(new DateOnly(2026, 10, 1), BillingPeriod.LocalToday(utc, tz));
        Assert.Equal(TimeZoneInfo.Utc, BillingPeriod.ResolveTimeZone("Nope/Zone", "Also/Bad"));
    }
}

public class TrafficEngineTests
{
    private static HeartbeatDto Hb(uint seq, ulong rx, ulong tx, uint elapsed = 2000) => new() { Seq = seq, ElapsedMs = elapsed, NetRxBytes = rx, NetTxBytes = tx };
    private static readonly DateOnly Today = new(2026, 9, 9);

    [Fact]
    public void FirstSampleBaselinesThenDeltasAccumulate()
    {
        var rt = new TrafficRuntime();
        var t0 = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
        var r0 = TrafficEngine.OnHeartbeat(rt, Hb(1, 1000, 500, 0), t0, "c1", t0.AddHours(-1), Today, 1);
        Assert.Equal(0, r0.DRx);
        var r1 = TrafficEngine.OnHeartbeat(rt, Hb(2, 3000, 1500), t0.AddSeconds(2), "c1", t0.AddHours(-1), Today, 1);
        Assert.Equal(2000, r1.DRx);
        Assert.Equal(1000, r1.DTx);
        Assert.Equal(1000, r1.RxBps);
        Assert.Equal(2000, rt.PerRx);
        Assert.Equal(2000, rt.DayRx);
        Assert.Equal(new DateOnly(2026, 9, 1), rt.PeriodStart);
        Assert.Equal(new DateOnly(2026, 10, 1), rt.PeriodEnd);
    }

    [Fact]
    public void RebootCountsNewCounterValueButUnexplainedDropIsIgnored()
    {
        var rt = new TrafficRuntime();
        var t0 = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
        var boot = t0.AddHours(-1);
        TrafficEngine.OnHeartbeat(rt, Hb(1, 10_000, 10_000, 0), t0, "c1", boot, Today, 1);
        // reboot: counters restart from a small value and boot time moved forward
        var r = TrafficEngine.OnHeartbeat(rt, Hb(1, 300, 200, 0), t0.AddSeconds(60), "c2", t0.AddSeconds(30), Today, 1);
        Assert.Equal(300, r.DRx);
        Assert.Equal(200, r.DTx);
        // counter goes backwards without a reboot -> dropped and re-baselined
        r = TrafficEngine.OnHeartbeat(rt, Hb(2, 100, 100), t0.AddSeconds(62), "c2", t0.AddSeconds(30), Today, 1);
        Assert.Equal(0, r.DRx);
        Assert.Equal(100, rt.PrevRx);
        Assert.Equal(300, rt.PerRx);
    }

    [Fact]
    public void ImplausibleDeltaIsDropped()
    {
        var rt = new TrafficRuntime();
        var t0 = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
        TrafficEngine.OnHeartbeat(rt, Hb(1, 0, 0, 0), t0, "c1", null, Today, 1);
        var warned = false;
        var r = TrafficEngine.OnHeartbeat(rt, Hb(2, 100_000_000_000_000, 0), t0.AddSeconds(2), "c1", null, Today, 1, _ => warned = true);
        Assert.Equal(0, r.DRx);
        Assert.True(warned);
    }

    [Fact]
    public void PeriodRolloverClosesThePreviousPeriod()
    {
        var rt = new TrafficRuntime();
        var t0 = new DateTime(2026, 9, 30, 23, 59, 0, DateTimeKind.Utc);
        TrafficEngine.OnHeartbeat(rt, Hb(1, 0, 0, 0), t0, "c1", null, new DateOnly(2026, 9, 30), 1);
        TrafficEngine.OnHeartbeat(rt, Hb(2, 1000, 1000), t0.AddSeconds(2), "c1", null, new DateOnly(2026, 9, 30), 1);
        TrafficEngine.OnHeartbeat(rt, Hb(3, 2000, 2000), t0.AddSeconds(4), "c1", null, new DateOnly(2026, 10, 1), 1);
        Assert.Single(rt.PendingClosed);
        Assert.Equal(1000, rt.PendingClosed[0].Rx);
        Assert.Equal(new DateOnly(2026, 9, 1), rt.PendingClosed[0].PeriodStart);
        Assert.Equal(1000, rt.PerRx);     // only the delta after the rollover
        Assert.True(rt.PeriodChanged);
        Assert.Single(rt.PendingDays);
    }
}

public class RollupTests(MasterFactory factory) : IClassFixture<MasterFactory>
{
    [Fact]
    public async Task HourlyRollupComputesWeightedAveragesAndSums()
    {
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<SnmDbContext>>();
        const int nodeId = 999_001;
        long hour = 1_800_000_000 / 3600 * 3600;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Metrics1m.Add(new Metric1m { NodeId = nodeId, Ts = hour, Samples = 30, CpuAvg = 100, CpuMax = 200, MemUsedAvgMb = 1000, MemUsedMaxMb = 1100, RxBpsAvg = 10, RxBpsMax = 20, RxBytes = 600, TxBytes = 300, DiskUsedMb = 50, DiskTotalMb = 100 });
            db.Metrics1m.Add(new Metric1m { NodeId = nodeId, Ts = hour + 60, Samples = 10, CpuAvg = 500, CpuMax = 900, MemUsedAvgMb = 2000, MemUsedMaxMb = 2100, RxBpsAvg = 30, RxBpsMax = 40, RxBytes = 400, TxBytes = 100, DiskUsedMb = 60, DiskTotalMb = 100 });
            await db.SaveChangesAsync();
            await Rollup.HourAsync(db, hour, CancellationToken.None);
            var row = await db.Metrics1h.SingleAsync(x => x.NodeId == nodeId && x.Ts == hour);
            Assert.Equal(40, row.Samples);
            Assert.Equal(200, row.CpuAvg);          // (100*30 + 500*10) / 40
            Assert.Equal(900, row.CpuMax);
            Assert.Equal(1250, row.MemUsedAvgMb);   // (1000*30 + 2000*10) / 40
            Assert.Equal(1000, row.RxBytes);
            Assert.Equal(400, row.TxBytes);
            Assert.Equal(40, row.RxBpsMax);
            // idempotent re-run
            await Rollup.HourAsync(db, hour, CancellationToken.None);
            Assert.Equal(1, await db.Metrics1h.CountAsync(x => x.NodeId == nodeId));
        }
    }
}
