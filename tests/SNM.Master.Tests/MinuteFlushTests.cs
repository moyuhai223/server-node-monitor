using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SNM.Master.Background;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Tests;

public class MinuteFlushTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 1, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTransactionRetainsRolloversAndRetryWritesThemOnce(bool failAtCommit)
    {
        await using var f = await Fixture.CreateAsync(failAtCommit ? new FailOnceCommit() : new FailOnceSave());
        await f.Flush.FlushAsync(Now, false, CancellationToken.None);

        Assert.Single(f.Node.Traffic.PendingDays);
        Assert.Single(f.Node.Traffic.PendingClosed);
        Assert.True(f.Node.Traffic.Dirty);
        Assert.True(f.Node.StateDirty);
        Assert.Single(f.Node.FlushQueue);
        await using (var db = f.Factory.CreateDbContext())
        {
            Assert.Empty(await db.TrafficDaily.ToListAsync());
            Assert.Empty(await db.TrafficMonthly.ToListAsync());
            Assert.Empty(await db.Metrics1m.ToListAsync());
            Assert.Empty(await db.TrafficStates.ToListAsync());
            Assert.Null((await db.Nodes.SingleAsync()).LastSeenAt);
        }

        await f.Flush.FlushAsync(Now.AddMinutes(1), false, CancellationToken.None);
        // Force a further write to prove that absolute totals are not accumulated twice.
        lock (f.Node.Sync) { f.Node.Traffic.Dirty = true; f.Node.StateDirty = true; }
        await f.Flush.FlushAsync(Now.AddMinutes(2), false, CancellationToken.None);
        await f.AssertPersistedAsync();
        Assert.Empty(f.Node.Traffic.PendingDays);
        Assert.Empty(f.Node.Traffic.PendingClosed);
        Assert.False(f.Node.Traffic.Dirty);
        Assert.False(f.Node.StateDirty);
        Assert.Empty(f.Node.FlushQueue);
    }

    [Fact]
    public async Task CancellationAfterSnapshotRestoresRetryWorkAndPropagates()
    {
        using var cts = new CancellationTokenSource();
        await using var f = await Fixture.CreateAsync(new CancelOnceSave(cts));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Flush.FlushAsync(Now, false, cts.Token));
        Assert.Single(f.Node.Traffic.PendingDays);
        Assert.Single(f.Node.Traffic.PendingClosed);
        Assert.True(f.Node.Traffic.Dirty);
        Assert.True(f.Node.StateDirty);
        Assert.Single(f.Node.FlushQueue);

        await f.Flush.FlushAsync(Now.AddMinutes(1), false, CancellationToken.None);
        await f.AssertPersistedAsync();
    }

    [Fact]
    public async Task CancellationWhileWaitingForWriterDoesNotDetachQueuedOrOpenMetrics()
    {
        await using var f = await Fixture.CreateAsync();
        f.Node.Acc = new MinuteAccumulator(new DateTimeOffset(Now).ToUnixTimeSeconds()) { Samples = 1, RxBytes = 13 };
        using (await f.WriteLock.AcquireAsync())
        {
            using var cts = new CancellationTokenSource();
            var pending = f.Flush.FlushAsync(Now, true, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Single(f.Node.FlushQueue);
            Assert.NotNull(f.Node.Acc);
            Assert.Single(f.Node.Traffic.PendingDays);
        }
        await f.Flush.FlushAsync(Now, true, CancellationToken.None);
        await using var db = f.Factory.CreateDbContext();
        Assert.Equal(2, (await db.Metrics1m.ToListAsync()).Sum(x => x.Samples));
        Assert.Equal(20, (await db.Metrics1m.ToListAsync()).Sum(x => x.RxBytes));
    }

    [Fact]
    public async Task CommitAcknowledgesOnlyItsSnapshotAndKeepsConcurrentRolloversDirty()
    {
        var gate = new PauseOnceSave();
        await using var f = await Fixture.CreateAsync(gate);
        var pending = f.Flush.FlushAsync(Now, false, CancellationToken.None);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // The transaction has not committed, so its original work must still be available.
            Assert.Single(f.Node.Traffic.PendingDays);
            Assert.Single(f.Node.Traffic.PendingClosed);
            lock (f.Node.Sync)
            {
                TrafficEngine.EnsurePeriod(f.Node.Traffic, new DateOnly(2026, 11, 1), 1);
                f.Node.Traffic.DayRx = f.Node.Traffic.PerRx = 30;
                f.Node.Traffic.DayTx = f.Node.Traffic.PerTx = 40;
                f.Node.LastSeenAt = Now.AddSeconds(2);
                f.Node.StateDirty = true;
            }
        }
        finally { gate.Release.TrySetResult(); await pending; }

        Assert.Equal(new DateOnly(2026, 10, 1), Assert.Single(f.Node.Traffic.PendingDays).Date);
        Assert.Equal(new DateOnly(2026, 10, 1), Assert.Single(f.Node.Traffic.PendingClosed).PeriodStart);
        Assert.True(f.Node.Traffic.Dirty);
        Assert.True(f.Node.StateDirty);
        await f.Flush.FlushAsync(Now.AddMonths(1), false, CancellationToken.None);
        await using var db = f.Factory.CreateDbContext();
        var months = await db.TrafficMonthly.OrderBy(x => x.PeriodStart).ToListAsync();
        Assert.Equal(3, months.Count);
        Assert.Equal(new long[] { 1000, 10, 30 }, months.Select(x => x.RxBytes));
        Assert.Equal(new long[] { 2000, 20, 40 }, months.Select(x => x.TxBytes));
        Assert.Equal(new[] { true, true, false }, months.Select(x => x.Closed));
        var days = await db.TrafficDaily.OrderBy(x => x.Date).ToListAsync();
        Assert.Equal(new long[] { 100, 10, 30 }, days.Select(x => x.RxBytes));
        Assert.Empty(f.Node.Traffic.PendingDays);
        Assert.Empty(f.Node.Traffic.PendingClosed);
        Assert.Equal(Now.AddSeconds(2), (await db.Nodes.SingleAsync()).LastSeenAt);
    }

    [Fact]
    public async Task OverlappingFlushWaitsBeforeCollectingNewMetrics()
    {
        var gate = new PauseOnceSave();
        await using var f = await Fixture.CreateAsync(gate);
        var first = f.Flush.FlushAsync(Now, false, CancellationToken.None);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Node.FlushQueue.Enqueue(new Metric1m { NodeId = f.Node.Id, Ts = 100, Samples = 1, RxBytes = 11 });
        var second = f.Flush.FlushAsync(Now.AddMinutes(1), false, CancellationToken.None);
        try { Assert.Single(f.Node.FlushQueue); }
        finally { gate.Release.TrySetResult(); await Task.WhenAll(first, second); }
        await using var db = f.Factory.CreateDbContext();
        var metric = await db.Metrics1m.SingleAsync();
        Assert.Equal(2, metric.Samples);
        Assert.Equal(18, metric.RxBytes);
        Assert.Empty(f.Node.Traffic.PendingDays);
        Assert.Empty(f.Node.Traffic.PendingClosed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _file;
        public SimpleFactory Factory { get; }
        public DbWriteLock WriteLock { get; } = new();
        public NodeRuntime Node { get; private set; } = null!;
        public MinuteFlushService Flush { get; private set; } = null!;

        private Fixture(string file, SimpleFactory factory) { _file = file; Factory = factory; }

        public static async Task<Fixture> CreateAsync(params IInterceptor[] interceptors)
        {
            var file = Path.Combine(Path.GetTempPath(), "snm-minute-flush-" + Guid.NewGuid().ToString("N") + ".db");
            var options = new DbContextOptionsBuilder<SnmDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
            var factory = new SimpleFactory(options);
            var f = new Fixture(file, factory);
            var meta = new Node { PublicName = "flush-regression", AgentKey = "local-fixture-key", TrafficResetDay = 1 };
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Nodes.Add(meta);
                await db.SaveChangesAsync();
            }
            var settings = new SettingsService(factory, NullLogger<SettingsService>.Instance);
            var registry = new NodeRegistry(factory, settings, NullLogger<NodeRegistry>.Instance);
            f.Node = registry.Upsert(meta);
            var t = f.Node.Traffic;
            t.Day = t.PeriodStart = new DateOnly(2026, 10, 1);
            t.PeriodEnd = new DateOnly(2026, 11, 1);
            t.DayRx = t.PerRx = 10; t.DayTx = t.PerTx = 20;
            t.PrevRx = 1010; t.PrevTx = 2020; t.PrevAt = Now;
            t.PendingDays.Add((new DateOnly(2026, 9, 30), 100, 200));
            t.PendingClosed.Add(new ClosedPeriod(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), 1000, 2000));
            t.Dirty = true;
            f.Node.LastSeenAt = Now; f.Node.StateDirty = true;
            f.Node.FlushQueue.Enqueue(new Metric1m { NodeId = meta.Id, Ts = 100, Samples = 1, RxBytes = 7 });
            var failingFactory = new SimpleFactory(new DbContextOptionsBuilder<SnmDbContext>(options).AddInterceptors(interceptors).Options);
            f.Flush = new MinuteFlushService(failingFactory, f.WriteLock, registry, NullLogger<MinuteFlushService>.Instance);
            return f;
        }

        public async Task AssertPersistedAsync()
        {
            await using var db = Factory.CreateDbContext();
            var days = await db.TrafficDaily.OrderBy(x => x.Date).ToListAsync();
            Assert.Equal(2, days.Count);
            Assert.Equal(new long[] { 100, 10 }, days.Select(x => x.RxBytes));
            Assert.Equal(new long[] { 200, 20 }, days.Select(x => x.TxBytes));
            var months = await db.TrafficMonthly.OrderBy(x => x.PeriodStart).ToListAsync();
            Assert.Equal(2, months.Count);
            Assert.Equal(new long[] { 1000, 10 }, months.Select(x => x.RxBytes));
            Assert.Equal(new long[] { 2000, 20 }, months.Select(x => x.TxBytes));
            Assert.True(months[0].Closed); Assert.False(months[1].Closed);
            var metric = await db.Metrics1m.SingleAsync();
            Assert.Equal(1, metric.Samples); Assert.Equal(7, metric.RxBytes);
            Assert.Equal(1010, (await db.TrafficStates.SingleAsync()).PrevRx);
            Assert.Equal(Now, (await db.Nodes.SingleAsync()).LastSeenAt);
        }

        public ValueTask DisposeAsync() { File.Delete(_file); return ValueTask.CompletedTask; }
    }

    private sealed class SimpleFactory(DbContextOptions<SnmDbContext> options) : IDbContextFactory<SnmDbContext>
    {
        public SnmDbContext CreateDbContext() => new(options);
    }

    private sealed class FailOnceSave : SaveChangesInterceptor
    {
        private bool _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_failed) { _failed = true; throw new IOException("P1-1: injected save failure"); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailOnceCommit : DbTransactionInterceptor
    {
        private bool _failed;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!_failed) { _failed = true; throw new IOException("P1-1: injected commit failure"); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelOnceSave(CancellationTokenSource cts) : SaveChangesInterceptor
    {
        private bool _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_failed) { _failed = true; cts.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PauseOnceSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_paused)
            {
                _paused = true;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}
