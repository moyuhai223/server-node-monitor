using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Master.Data.Entities;

namespace SNM.Master.Runtime;

/// <summary>Billing-period arithmetic (docs/DATA.md 4.4): reset day + month-end clamping.</summary>
public static class BillingPeriod
{
    public static DateOnly PeriodStart(DateOnly today, int resetDay)
    {
        resetDay = Math.Clamp(resetDay, 1, 31);
        var d = Math.Min(resetDay, DateTime.DaysInMonth(today.Year, today.Month));
        if (today.Day >= d) return new DateOnly(today.Year, today.Month, d);
        var pm = today.AddMonths(-1);
        var d2 = Math.Min(resetDay, DateTime.DaysInMonth(pm.Year, pm.Month));
        return new DateOnly(pm.Year, pm.Month, d2);
    }

    /// <summary>Exclusive end of the period that starts at <paramref name="start"/>.</summary>
    public static DateOnly PeriodEnd(DateOnly start, int resetDay)
    {
        resetDay = Math.Clamp(resetDay, 1, 31);
        var nm = start.AddMonths(1);
        var d = Math.Min(resetDay, DateTime.DaysInMonth(nm.Year, nm.Month));
        return new DateOnly(nm.Year, nm.Month, d);
    }

    public static long Billed(long rx, long tx, int countMode) => countMode switch
    {
        TrafficCountMode.TxOnly => tx,
        TrafficCountMode.RxOnly => rx,
        TrafficCountMode.MaxOfRxTx => Math.Max(rx, tx),
        _ => rx + tx,
    };

    public static TimeZoneInfo ResolveTimeZone(string? nodeTz, string? siteTz)
    {
        foreach (var id in new[] { nodeTz, siteTz })
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        }
        return TimeZoneInfo.Utc;
    }

    public static DateOnly LocalToday(DateTime utcNow, TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz));
}

/// <summary>A closed billing period waiting to be written (Closed = 1).</summary>
public sealed record ClosedPeriod(DateOnly PeriodStart, DateOnly PeriodEnd, long Rx, long Tx);

/// <summary>Per-node traffic state (docs/DATA.md 4). Mirrors TrafficState + the current TrafficDaily/TrafficMonthly rows.</summary>
public sealed class TrafficRuntime
{
    public long PrevRx = -1, PrevTx = -1;
    public DateTime PrevAt;
    public DateTime? BootTimeAtPrev;
    public string? PrevConnectionId;

    public DateOnly Day; public long DayRx, DayTx;
    public DateOnly PeriodStart, PeriodEnd; public long PerRx, PerTx;

    /// <summary>Periods rolled over since the last flush; written with Closed = 1.</summary>
    public readonly List<ClosedPeriod> PendingClosed = [];
    /// <summary>Days rolled over since the last flush (their final totals).</summary>
    public readonly List<(DateOnly Date, long Rx, long Tx)> PendingDays = [];

    public bool Dirty;

    /// <summary>Set when the period changed so the alert engine can silently reset traffic alert states.</summary>
    public bool PeriodChanged;

    public void ResetBaseline()
    {
        PrevRx = PrevTx = -1;
        PrevConnectionId = null;
        Dirty = true;
    }

    public void ResetPeriodCounters()
    {
        PerRx = PerTx = 0;
        Dirty = true;
    }
}

public readonly record struct TrafficResult(long DRx, long DTx, long RxBps, long TxBps);

/// <summary>Delta engine (docs/DATA.md 4.2). Pure in-memory; persisted by MinuteFlushService.</summary>
public static class TrafficEngine
{
    /// <summary>100 Gbit/s plausibility ceiling.</summary>
    public const long MaxBytesPerSecond = 12_500_000_000;

    public static long Delta(long cur, long prev, bool rebooted)
    {
        if (cur >= prev) return cur - prev;   // normal monotonic increase
        if (rebooted) return cur;              // counters were reset by a system reboot: everything since boot is new
        return 0;                              // counter went backwards without a reboot: drop this delta, re-baseline
    }

    /// <summary>Ensures Day/Period match <paramref name="today"/>; rolls counters over when they changed.</summary>
    public static void EnsurePeriod(TrafficRuntime rt, DateOnly today, int resetDay)
    {
        var start = BillingPeriod.PeriodStart(today, resetDay);
        if (start != rt.PeriodStart)
        {
            if (rt.PeriodStart != default)
            {
                rt.PendingClosed.Add(new ClosedPeriod(rt.PeriodStart, rt.PeriodEnd, rt.PerRx, rt.PerTx));
                rt.PeriodChanged = true;
            }
            rt.PeriodStart = start;
            rt.PeriodEnd = BillingPeriod.PeriodEnd(start, resetDay);
            rt.PerRx = rt.PerTx = 0;
            rt.Dirty = true;
        }
        else if (rt.PeriodEnd != BillingPeriod.PeriodEnd(start, resetDay))
        {
            rt.PeriodEnd = BillingPeriod.PeriodEnd(start, resetDay);
            rt.Dirty = true;
        }

        if (rt.Day != today)
        {
            if (rt.Day != default) rt.PendingDays.Add((rt.Day, rt.DayRx, rt.DayTx));
            rt.Day = today;
            rt.DayRx = rt.DayTx = 0;
            rt.Dirty = true;
        }
    }

    public static TrafficResult OnHeartbeat(TrafficRuntime rt, HeartbeatDto hb, DateTime now, string connectionId, DateTime? bootTimeUtc,
        DateOnly today, int resetDay, Action<string>? warn = null)
    {
        EnsurePeriod(rt, today, resetDay);

        var cur = new { Rx = unchecked((long)hb.NetRxBytes), Tx = unchecked((long)hb.NetTxBytes) };

        if (rt.PrevRx < 0 || rt.PrevTx < 0)
        {
            Baseline(rt, cur.Rx, cur.Tx, now, connectionId, bootTimeUtc);
            return new TrafficResult(0, 0, 0, 0);
        }

        var rebooted = bootTimeUtc is { } b && rt.BootTimeAtPrev is { } pb && Math.Abs((b - pb).TotalSeconds) > 120;
        var dRx = Delta(cur.Rx, rt.PrevRx, rebooted);
        var dTx = Delta(cur.Tx, rt.PrevTx, rebooted);

        var dtServer = Math.Max((now - rt.PrevAt).TotalSeconds, 0.2);
        var elapsedTrusted = hb.Seq > 1 && connectionId == rt.PrevConnectionId && hb.ElapsedMs >= 200 && hb.ElapsedMs <= 120_000;
        var rateDt = elapsedTrusted ? hb.ElapsedMs / 1000.0 : dtServer;

        var maxPlausible = (long)(Math.Max(dtServer, rateDt) * MaxBytesPerSecond);
        if (dRx > maxPlausible) { warn?.Invoke($"implausible rx delta {dRx} over {rateDt:F1}s dropped"); dRx = 0; }
        if (dTx > maxPlausible) { warn?.Invoke($"implausible tx delta {dTx} over {rateDt:F1}s dropped"); dTx = 0; }

        var rxBps = (long)Math.Round(dRx / rateDt);
        var txBps = (long)Math.Round(dTx / rateDt);

        rt.DayRx += dRx; rt.DayTx += dTx;
        rt.PerRx += dRx; rt.PerTx += dTx;
        Baseline(rt, cur.Rx, cur.Tx, now, connectionId, bootTimeUtc);
        return new TrafficResult(dRx, dTx, rxBps, txBps);
    }

    private static void Baseline(TrafficRuntime rt, long rx, long tx, DateTime now, string connectionId, DateTime? bootTimeUtc)
    {
        rt.PrevRx = rx; rt.PrevTx = tx; rt.PrevAt = now;
        rt.BootTimeAtPrev = bootTimeUtc; rt.PrevConnectionId = connectionId;
        rt.Dirty = true;
    }

    public static void LoadFrom(TrafficRuntime rt, TrafficState? state, TrafficMonthly? monthly, TrafficDaily? daily, DateOnly today, int resetDay)
    {
        if (state is not null)
        {
            rt.PrevRx = state.PrevRx; rt.PrevTx = state.PrevTx;
            rt.PrevAt = state.PrevAtUtc ?? DateTime.MinValue;
            rt.BootTimeAtPrev = state.BootTimeAtPrevUtc;
            rt.PrevConnectionId = state.PrevConnectionId;
        }
        rt.PeriodStart = BillingPeriod.PeriodStart(today, resetDay);
        rt.PeriodEnd = BillingPeriod.PeriodEnd(rt.PeriodStart, resetDay);
        if (monthly is not null && monthly.PeriodStart == rt.PeriodStart) { rt.PerRx = monthly.RxBytes; rt.PerTx = monthly.TxBytes; }
        else { rt.PerRx = rt.PerTx = 0; }
        rt.Day = today;
        if (daily is not null && daily.Date == today) { rt.DayRx = daily.RxBytes; rt.DayTx = daily.TxBytes; }
        else { rt.DayRx = rt.DayTx = 0; }
        rt.Dirty = false;
        rt.PeriodChanged = false;
        rt.PendingClosed.Clear();
        rt.PendingDays.Clear();
    }
}
