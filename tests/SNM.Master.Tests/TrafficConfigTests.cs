using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SNM.Contracts.Dtos;
using SNM.Master.Background;
using SNM.Master.Alerting;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Runtime;
using SNM.Master.Services;
using Xunit.Abstractions;

namespace SNM.Master.Tests;

public sealed class TrafficConfigTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("limitBytes", 2000, 300)]
    [InlineData("countMode", 1, 200)]
    [InlineData("zone", 0, 300)]
    public async Task EditingPolicyPreservesLiveCountersAndPersistsBaseline(string field, int value, long billed)
    {
        using var f = new Fixture();
        var body = field == "zone" ? "{\"timeZoneId\":\"Etc/UTC\"}" : JsonSerializer.Serialize(new { traffic = new Dictionary<string, int> { [field] = value } });
        var detail = await f.Patch(body);
        output.WriteLine($"{field}: before=100/200; after={detail.Traffic.RxBytes}/{detail.Traffic.TxBytes}; billed={detail.Traffic.BilledBytes}");
        Assert.Equal((100L, 200L, billed), (detail.Traffic.RxBytes, detail.Traffic.TxBytes, detail.Traffic.BilledBytes));
        Assert.False(f.Node.Traffic.PeriodChanged);
        await using (var db = f.Factory.CreateDbContext())
        {
            var m = await db.TrafficMonthly.SingleAsync();
            Assert.Equal((100L, 200L, billed), (m.RxBytes, m.TxBytes, m.BilledBytes));
            Assert.Equal((detail.Traffic.LimitBytes, detail.Traffic.CountMode), (m.LimitBytes, m.CountMode));
            var state = await db.TrafficStates.SingleAsync();
            Assert.Equal((1100L, 2200L), (state.PrevRx, state.PrevTx));
        }
        f.Heartbeat(3, 1110, 2220);
        await f.Flush.FlushAsync(f.Now.AddMinutes(1), true, default);
        await using var verify = f.Factory.CreateDbContext();
        var day = await verify.TrafficDaily.SingleAsync();
        Assert.Equal((110L, 220L), (day.RxBytes, day.TxBytes));
        var monthly = await verify.TrafficMonthly.SingleAsync();
        Assert.Equal((110L, 220L), (monthly.RxBytes, monthly.TxBytes));
    }

    [Fact]
    public async Task ChangingResetDayClosesOldPeriodAndReopensAnExistingBucketWithoutOverwritingIt()
    {
        using var f = new Fixture(); var t = f.Node.Traffic;
        var original = t.PeriodStart;
        var target = BillingPeriod.PeriodStart(f.Today, 15);
        using (var db = f.Factory.CreateDbContext())
        {
            db.TrafficMonthly.Add(new TrafficMonthly { NodeId = f.Node.Id, PeriodStart = target, PeriodEnd = BillingPeriod.PeriodEnd(target, 15), RxBytes = 7, TxBytes = 8, Closed = true });
            db.SaveChanges();
        }
        t.PendingDays.Add((f.Today.AddDays(-2), 31, 41));
        var detail = await f.Patch("""{"traffic":{"resetDay":15,"countMode":1,"limitBytes":2000}}""");
        Assert.Equal((target, 7L, 8L), (detail.Traffic.PeriodStart, t.PerRx, t.PerTx));
        Assert.True(t.PeriodChanged);
        using (var db = f.Factory.CreateDbContext())
        {
            var old = await db.TrafficMonthly.SingleAsync(x => x.PeriodStart == original);
            Assert.Equal((true, 100L, 200L, 300L, 1, 0, 1000L), (old.Closed, old.RxBytes, old.TxBytes, old.BilledBytes, old.ResetDay, old.CountMode, old.LimitBytes));
            Assert.Equal(31L, (await db.TrafficDaily.SingleAsync(x => x.Date == f.Today.AddDays(-2))).RxBytes);
            Assert.False((await db.TrafficMonthly.SingleAsync(x => x.PeriodStart == target)).Closed);
        }
        f.Heartbeat(3, 1110, 2220);
        await f.Patch("""{"traffic":{"resetDay":1}}""");
        Assert.Equal((original, 100L, 200L), (t.PeriodStart, t.PerRx, t.PerTx));
        Assert.Equal((110L, 220L), (t.DayRx, t.DayTx));
        using var check = f.Factory.CreateDbContext();
        var closed = await check.TrafficMonthly.SingleAsync(x => x.PeriodStart == target);
        Assert.Equal((true, 17L, 28L, 28L), (closed.Closed, closed.RxBytes, closed.TxBytes, closed.BilledBytes));
    }

    [Fact]
    public async Task ChangingZoneMovesTheDayWithoutMovingOldBytesIntoANewPeriod()
    {
        using var f = new Fixture("Etc/GMT+10"); // UTC-10 to UTC+14: adjacent local dates
        var t = f.Node.Traffic;
        var oldDay = t.Day;
        var oldStart = t.PeriodStart;
        var detail = await f.Patch("""{"timeZoneId":"Etc/GMT-14","traffic":{"resetDay":15}}""");
        Assert.NotEqual(oldStart, detail.Traffic.PeriodStart);
        Assert.Equal((0L, 0L), (detail.Traffic.RxBytes, detail.Traffic.TxBytes));
        Assert.Equal(oldDay.AddDays(1), t.Day);
        Assert.Equal((0L, 0L), (t.DayRx, t.DayTx));
        Assert.Equal((1100L, 2200L), (t.PrevRx, t.PrevTx));
        f.Heartbeat(3, 1110, 2220);
        await f.Flush.FlushAsync(f.Now.AddMinutes(1), true, default);
        using var db = f.Factory.CreateDbContext();
        var old = await db.TrafficDaily.SingleAsync(x => x.Date == oldDay);
        var current = await db.TrafficDaily.SingleAsync(x => x.Date == t.Day);
        Assert.Equal((100L, 200L), (old.RxBytes, old.TxBytes));
        Assert.Equal((10L, 20L), (current.RxBytes, current.TxBytes));
        Assert.True((await db.TrafficMonthly.SingleAsync(x => x.PeriodStart == oldStart)).Closed);
    }

    [Fact]
    public async Task HeartbeatWhilePatchWaitsForWriterIsCountedExactlyOnce()
    {
        using var f = new Fixture(); Task<SNM.Master.Api.Dto.NodeDetailDto> patch;
        using (await f.Writer.AcquireAsync())
        {
            patch = f.Patch("""{"traffic":{"limitBytes":5000}}""");
            Assert.False(patch.IsCompleted);
            f.Heartbeat(3, 1150, 2275);
        }
        var detail = await patch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((150L, 275L), (detail.Traffic.RxBytes, detail.Traffic.TxBytes));
        f.Heartbeat(4, 1155, 2280);
        await f.Flush.FlushAsync(f.Now.AddMinutes(1), true, default);
        using var db = f.Factory.CreateDbContext(); var m = await db.TrafficMonthly.SingleAsync();
        Assert.Equal((155L, 280L), (m.RxBytes, m.TxBytes));
        Assert.Equal(1155L, (await db.TrafficStates.SingleAsync()).PrevRx);
    }

    [Fact]
    public async Task FailedPolicyTransactionKeepsOldConfigAndDirtyWorkForRetry()
    {
        using var f = new Fixture(); var t = f.Node.Traffic;
        t.PendingDays.Add((f.Today.AddDays(-2), 4, 5));
        using var db = f.Factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_traffic BEFORE INSERT ON TrafficMonthly BEGIN SELECT RAISE(ABORT, 'injected traffic write failure'); END");
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Patch("""{"traffic":{"limitBytes":5000}}"""));
        Assert.Equal(1000L, (await db.Nodes.AsNoTracking().SingleAsync()).TrafficLimitBytes);
        Assert.Equal(1000L, f.Node.Meta.TrafficLimitBytes);
        Assert.Equal((100L, 200L, 1100L), (t.PerRx, t.PerTx, t.PrevRx));
        Assert.True(t.Dirty); Assert.Single(t.PendingDays); Assert.Empty(await db.TrafficDaily.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_traffic");
        await f.Patch("""{"traffic":{"limitBytes":5000}}""");
        Assert.Empty(t.PendingDays); Assert.Equal(2, await db.TrafficDaily.CountAsync());
    }

    [Fact]
    public async Task HeartbeatArrivingDuringSaveIsIncludedByTheNextFlush()
    {
        var pause = new PauseSave();
        using var f = new Fixture(pause);
        pause.Armed = true;
        var patch = Task.Run(() => f.Patch("""{"traffic":{"limitBytes":9000}}"""));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = Task.Run(() => { started.SetResult(); f.Heartbeat(3, 1150, 2275); });
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { pause.Release.Set(); }
        await Task.WhenAll(patch, heartbeat).WaitAsync(TimeSpan.FromSeconds(10));
        await f.Flush.FlushAsync(f.Now.AddMinutes(1), true, default);
        using var db = f.Factory.CreateDbContext();
        Assert.Equal((150L, 275L), (f.Node.Traffic.PerRx, f.Node.Traffic.PerTx));
        Assert.Equal(150L, (await db.TrafficMonthly.SingleAsync()).RxBytes);
        Assert.Equal(2275L, (await db.TrafficStates.SingleAsync()).PrevTx);
    }

    private sealed class PauseSave : SaveChangesInterceptor
    {
        public bool Armed;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        private void Pause()
        {
            if (!Armed) return;
            Armed = false; Entered.SetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }
        public override InterceptionResult<int> SavingChanges(DbContextEventData e, InterceptionResult<int> r) { Pause(); return r; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct = default) { Pause(); return ValueTask.FromResult(r); }
    }

    private sealed class FactoryImpl(DbContextOptions<SnmDbContext> options) : IDbContextFactory<SnmDbContext>
    { public SnmDbContext CreateDbContext() => new(options); }
    private sealed class Fixture : IDisposable
    {
        private readonly string file = Path.Combine(Path.GetTempPath(), $"snm-traffic-config-{Guid.NewGuid():N}.db");
        public readonly IDbContextFactory<SnmDbContext> Factory;
        public readonly DbWriteLock Writer = new();
        public readonly NodeRuntime Node;
        public readonly MinuteFlushService Flush;
        private readonly NodeService nodes;
        private readonly NodeIngestService ingest;
        public readonly DateTime Now = DateTime.UtcNow;
        public DateOnly Today => DateOnly.FromDateTime(Now);
        public Fixture(params IInterceptor[] interceptors) : this("UTC", interceptors) { }
        public Fixture(string zone, params IInterceptor[] interceptors)
        {
            Factory = new FactoryImpl(new DbContextOptionsBuilder<SnmDbContext>().UseSqlite($"Data Source={file};Pooling=False").AddInterceptors(interceptors).Options);
            using var db = Factory.CreateDbContext(); db.Database.EnsureCreated();
            var meta = new Node { PublicName = "fixture", AgentKey = "fixture-key", TimeZoneId = zone, TrafficLimitBytes = 1000, TrafficResetDay = 1 };
            db.Nodes.Add(meta); db.SaveChanges();
            var settings = new SettingsService(Factory, NullLogger<SettingsService>.Instance);
            var registry = new NodeRegistry(Factory, settings, NullLogger<NodeRegistry>.Instance);
            Node = registry.Upsert(meta); Node.ConnectionId = "fixture"; Node.Registered = true;
            var tracker = new AgentConnectionTracker();
            var alerts = new AlertEngine(Factory, settings, null!, null!, registry, NullLogger<AlertEngine>.Instance);
            nodes = new NodeService(Factory, Writer, registry, settings, alerts, tracker, null!, NullLogger<NodeService>.Instance);
            ingest = new NodeIngestService(registry, Factory, null!, settings, tracker, NullLogger<NodeIngestService>.Instance);
            Flush = new MinuteFlushService(Factory, Writer, registry, NullLogger<MinuteFlushService>.Instance);
            Heartbeat(1, 1000, 2000); Heartbeat(2, 1100, 2200);
        }
        public Task<SNM.Master.Api.Dto.NodeDetailDto> Patch(string text) => nodes.UpdateAsync(Node.Id, JsonSerializer.Deserialize<JsonElement>(text), default);
        public void Heartbeat(uint seq, ulong rx, ulong tx) => Assert.Equal(DropReason.None, ingest.OnHeartbeat(Node, new HeartbeatDto { Seq = seq, ElapsedMs = 1000, NetRxBytes = rx, NetTxBytes = tx }, "fixture", Now));
        public void Dispose() => File.Delete(file);
    }
}
