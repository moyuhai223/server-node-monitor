using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Master.Background;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Hubs;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Alerting;

/// <summary>Rule evaluation + Normal/Pending/Firing state machine + event creation (docs/DATA.md 5). Runs every 10 s.</summary>
public sealed class AlertEngine : SnmBackgroundService
{
    private sealed record StateKey(int NodeId, int Rule, string Subject);

    private sealed class State
    {
        public int Phase;               // 0 Normal / 1 Pending / 2 Firing
        public int Consecutive;
        public int ResolveCount;
        public DateTime? FiringSince;
        public DateTime? LastNotifiedAt;
        public DateTime? CooldownUntil;
        public double LastValue;
        public long? OpenEventId;
        public bool Dirty;
    }

    private readonly record struct RuleSpec(int Consecutive, int ResolveConsecutive, int Severity);

    private readonly IDbContextFactory<SnmDbContext> _dbFactory;
    private readonly SettingsService _settings;
    private readonly NotificationDispatcher _dispatcher;
    private readonly IHubContext<AdminHub> _adminHub;
    private readonly ConcurrentDictionary<StateKey, State> _states = new();
    private readonly Lock _evalLock = new();

    public AlertEngine(IDbContextFactory<SnmDbContext> dbFactory, SettingsService settings, NotificationDispatcher dispatcher, IHubContext<AdminHub> adminHub,
        NodeRegistry registry, ILogger<AlertEngine> logger) : base(logger, registry)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _dispatcher = dispatcher;
        _adminHub = adminHub;
    }

    protected override TimeSpan Period => TimeSpan.FromSeconds(10);

    public int FiringCount => _states.Values.Count(s => s.Phase == 2);

    protected override async Task OnStartAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var row in await db.AlertStates.AsNoTracking().ToListAsync(ct))
        {
            _states[new StateKey(row.NodeId, row.Rule, row.Subject)] = new State
            {
                Phase = row.State, Consecutive = row.Consecutive, ResolveCount = row.ResolveCount, FiringSince = row.FiringSince,
                LastNotifiedAt = row.LastNotifiedAt, CooldownUntil = row.CooldownUntil, LastValue = row.LastValue, OpenEventId = row.OpenEventId,
            };
        }
        Logger.LogInformation("Loaded {Count} alert states ({Firing} firing)", _states.Count, FiringCount);
    }

    protected override async Task TickAsync(DateTime now, CancellationToken ct)
    {
        var s = _settings.Snapshot;
        foreach (var node in Registry.All)
        {
            try { await EvaluateNodeAsync(node, s, now, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Logger.LogError(ex, "Alert evaluation failed for node {NodeId}", node.Id); }
        }
        await MaybeRunExpiryCheckAsync(now, s, ct);
        await PersistDirtyStatesAsync(now, ct);
    }

    private async Task EvaluateNodeAsync(NodeRuntime node, SettingsSnapshot s, DateTime now, CancellationToken ct)
    {
        var meta = node.Meta;
        if (!meta.Enabled) return;

        if (node.Traffic.PeriodChanged)
        {
            node.Traffic.PeriodChanged = false;
            await ResetTrafficStatesAsync(node, now, ct);
        }

        // 1. Offline
        var offlineSec = meta.OfflineAlertSec ?? s.OfflineTimeoutSec;
        var offline = meta.FirstSeenAt is not null && node.LastSeenAt is { } seen && (now - seen).TotalSeconds > offlineSec;
        var offlineFor = node.LastSeenAt is { } ls ? (now - ls).TotalSeconds : 0;
        await EvaluateAsync(node, AlertRule.Offline, "", offline, offlineFor, offlineSec, new RuleSpec(s.OfflineConsecutive, 1, AlertSeverity.Critical), now, null, ct);

        // 2. CPU high (mean of the last 60 s of points; needs >= 10 points)
        var cpuPct = meta.CpuAlertPct ?? s.CpuPct;
        var cutoff = LiveSnapshotBuilder.UnixMs(now) - 60_000;
        var pts = node.History.Snapshot().Where(p => p.Ts >= cutoff).ToArray();
        var cpuAvg = pts.Length >= 10 ? pts.Average(p => p.Cpu) : 0;
        var cpuState = GetState(node.Id, AlertRule.CpuHigh, "");
        bool cpuCond;
        if (cpuState.Phase == 2) cpuCond = !(pts.Length >= 10 && cpuAvg < (cpuPct - 10) * 10) && node.Status == NodeStatus.Online;
        else cpuCond = pts.Length >= 10 && cpuAvg >= cpuPct * 10;
        await EvaluateAsync(node, AlertRule.CpuHigh, "", cpuCond, cpuAvg, cpuPct, new RuleSpec(Math.Max(1, s.CpuSustainMin * 6), 12, AlertSeverity.Warning), now, null, ct);

        // 3/4. Traffic
        if (meta.TrafficLimitBytes > 0)
        {
            var billed = BillingPeriod.Billed(node.Traffic.PerRx, node.Traffic.PerTx, meta.TrafficCountMode);
            var pct = billed * 100.0 / meta.TrafficLimitBytes;
            var warnPct = meta.TrafficAlertPct ?? s.TrafficWarnPct;
            var subject = node.Traffic.PeriodStart.ToString("yyyy-MM-dd");
            var extra = $"{AlertTexts.FormatBytes(billed)} / {AlertTexts.FormatBytes(meta.TrafficLimitBytes)},账期 {node.Traffic.PeriodStart:MM-dd} ~ {node.Traffic.PeriodEnd.AddDays(-1):MM-dd}";
            await EvaluateAsync(node, AlertRule.TrafficWarn, subject, pct >= warnPct, pct, warnPct, new RuleSpec(1, 1, AlertSeverity.Warning), now, extra, ct);
            await EvaluateAsync(node, AlertRule.TrafficExceeded, subject, pct >= 100, pct, 100, new RuleSpec(1, 1, AlertSeverity.Critical), now, extra, ct);
        }

        // 6. Disk (optional)
        if (s.DiskEnabled && node.Live is { } live && node.Status == NodeStatus.Online)
        {
            var diskPct = meta.DiskAlertPct ?? s.DiskPct;
            var disks = node.Disks;
            for (var i = 0; i < disks.Length && i < live.DiskUsedMb.Length; i++)
            {
                if (disks[i].TotalMb == 0) continue;
                var used = live.DiskUsedMb[i] * 100.0 / disks[i].TotalMb;
                var st = GetState(node.Id, AlertRule.DiskHigh, disks[i].Mount);
                var cond = st.Phase == 2 ? used >= diskPct - 5 : used >= diskPct;
                await EvaluateAsync(node, AlertRule.DiskHigh, disks[i].Mount, cond, used, diskPct, new RuleSpec(3, 3, AlertSeverity.Warning), now, null, ct);
            }
        }
    }

    private State GetState(int nodeId, int rule, string subject) => _states.GetOrAdd(new StateKey(nodeId, rule, subject), _ => new State());

    private async Task EvaluateAsync(NodeRuntime node, int rule, string subject, bool cond, double value, double threshold, RuleSpec spec, DateTime now, string? extra, CancellationToken ct)
    {
        var st = GetState(node.Id, rule, subject);
        st.LastValue = value;
        switch (st.Phase)
        {
            case 0:
                if (cond) { st.Phase = 1; st.Consecutive = 1; st.Dirty = true; await TryFireAsync(node, rule, subject, st, spec, value, threshold, now, extra, ct); }
                break;
            case 1:
                if (cond) { st.Consecutive++; st.Dirty = true; await TryFireAsync(node, rule, subject, st, spec, value, threshold, now, extra, ct); }
                else { st.Phase = 0; st.Consecutive = 0; st.Dirty = true; }
                break;
            case 2:
                if (!cond)
                {
                    st.ResolveCount++;
                    st.Dirty = true;
                    if (st.ResolveCount >= spec.ResolveConsecutive) await ResolveAsync(node, rule, st, now, silent: false, extra, ct);
                }
                else
                {
                    if (st.ResolveCount != 0) { st.ResolveCount = 0; st.Dirty = true; }
                    await MaybeRepeatAsync(node, rule, subject, st, value, threshold, now, extra, ct);
                }
                break;
        }
    }

    private async Task TryFireAsync(NodeRuntime node, int rule, string subject, State st, RuleSpec spec, double value, double threshold, DateTime now, string? extra, CancellationToken ct)
    {
        if (st.Consecutive < spec.Consecutive) return;
        var s = _settings.Snapshot;
        var meta = node.Meta;
        st.Phase = 2; st.FiringSince = now; st.ResolveCount = 0;
        var (title, message) = AlertTexts.Firing(rule, meta, value, threshold, subject, s.TimeZone, now, extra);
        var canNotify = s.AlertEnabled && meta.AlertsEnabled && (st.CooldownUntil is null || now >= st.CooldownUntil);
        var ev = new AlertEvent
        {
            NodeId = node.Id, NodeName = meta.PublicName, Rule = rule, Subject = subject, Status = AlertEventStatus.Firing, Severity = spec.Severity,
            Title = title, Message = message, Value = value, Threshold = threshold, DedupKey = $"{rule}:{node.Id}:{subject}", StartedAt = now, Notified = canNotify,
        };
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.AlertEvents.Add(ev);
            await db.SaveChangesAsync(ct);
        }
        st.OpenEventId = ev.Id;
        st.Dirty = true;
        if (canNotify)
        {
            st.LastNotifiedAt = now;
            st.CooldownUntil = now.AddMinutes(Math.Clamp(s.CooldownMin, 1, 1440));
            _dispatcher.Enqueue(new NotificationJob(ev, DeliveryKind.Firing, meta));
        }
        Logger.LogWarning("ALERT {Title}: {Message} (notify={Notify})", title, message, canNotify);
        await BroadcastAsync(ev, ct);
    }

    private async Task ResolveAsync(NodeRuntime node, int rule, State st, DateTime now, bool silent, string? extra, CancellationToken ct)
    {
        var meta = node.Meta;
        AlertEvent? ev = null;
        if (st.OpenEventId is { } id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            ev = await db.AlertEvents.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (ev is not null && ev.Status != AlertEventStatus.Resolved)
            {
                ev.Status = AlertEventStatus.Resolved;
                ev.ResolvedAt = now;
                await db.SaveChangesAsync(ct);
            }
        }
        var firingSince = st.FiringSince ?? now;
        st.Phase = 0; st.Consecutive = 0; st.ResolveCount = 0; st.OpenEventId = null; st.FiringSince = null; st.Dirty = true;
        if (ev is null) return;
        var (title, message) = AlertTexts.Recovery(rule, meta, firingSince, now, extra);
        if (ev.Notified && !silent)
        {
            var recovery = new AlertEvent
            {
                Id = ev.Id, NodeId = ev.NodeId, NodeName = ev.NodeName, Rule = rule, Subject = ev.Subject, Status = AlertEventStatus.Resolved, Severity = ev.Severity,
                Title = title, Message = message, Value = ev.Value, Threshold = ev.Threshold, DedupKey = ev.DedupKey, StartedAt = ev.StartedAt, ResolvedAt = now, Notified = true,
            };
            _dispatcher.Enqueue(new NotificationJob(recovery, DeliveryKind.Recovery, meta));
        }
        Logger.LogInformation("RESOLVED {Title}: {Message}", title, message);
        await BroadcastAsync(ev, ct);
    }

    private async Task MaybeRepeatAsync(NodeRuntime node, int rule, string subject, State st, double value, double threshold, DateTime now, string? extra, CancellationToken ct)
    {
        var s = _settings.Snapshot;
        if (s.RepeatMin <= 0 || st.LastNotifiedAt is not { } last || (now - last).TotalMinutes < s.RepeatMin || st.OpenEventId is not { } id) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ev = await db.AlertEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (ev is null || !ev.Notified) return;
        var (title, message) = AlertTexts.Firing(rule, node.Meta, value, threshold, subject, s.TimeZone, now, extra);
        var repeat = new AlertEvent
        {
            Id = ev.Id, NodeId = ev.NodeId, NodeName = ev.NodeName, Rule = rule, Subject = subject, Status = AlertEventStatus.Firing, Severity = ev.Severity,
            Title = title + "(仍在持续)", Message = message, Value = value, Threshold = threshold, DedupKey = ev.DedupKey, StartedAt = ev.StartedAt, Notified = true,
        };
        st.LastNotifiedAt = now; st.Dirty = true;
        _dispatcher.Enqueue(new NotificationJob(repeat, DeliveryKind.Firing, node.Meta));
    }

    /// <summary>Billing period rolled over: close traffic alerts silently.</summary>
    public async Task ResetTrafficStatesAsync(NodeRuntime node, DateTime now, CancellationToken ct)
    {
        foreach (var (key, st) in _states.Where(kv => kv.Key.NodeId == node.Id && kv.Key.Rule is AlertRule.TrafficWarn or AlertRule.TrafficExceeded).ToList())
        {
            if (st.Phase == 2) await ResolveAsync(node, key.Rule, st, now, silent: true, null, ct);
            else if (st.Phase == 1) { st.Phase = 0; st.Consecutive = 0; st.Dirty = true; }
        }
    }

    /// <summary>Daily expiry scan at alert.expiryCheckHour (site time zone); catches up after restarts.</summary>
    private async Task MaybeRunExpiryCheckAsync(DateTime now, SettingsSnapshot s, CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, s.TimeZone);
        if (local.Hour < s.ExpiryCheckHour) return;
        var today = DateOnly.FromDateTime(local).ToString("yyyy-MM-dd");
        if (_settings.GetString("alert.lastExpiryCheckDate") == today) return;
        foreach (var node in Registry.All) await EvaluateExpiryAsync(node, now, ct);
        await _settings.SetInternalAsync("alert.lastExpiryCheckDate", SettingsService.J(today), ct);
        Logger.LogInformation("Expiry check done for {Date}", today);
    }

    /// <summary>Evaluates the expiry rule for one node now (also called by NodeService when ExpiresAt changes).</summary>
    public async Task EvaluateExpiryAsync(NodeRuntime node, DateTime now, CancellationToken ct)
    {
        var s = _settings.Snapshot;
        var meta = node.Meta;
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, s.TimeZone));
        string? current = null;
        if (meta.Enabled && meta.ExpiresAt is { } exp)
        {
            var daysLeft = exp.DayNumber - todayLocal.DayNumber;
            current = exp.ToString("yyyy-MM-dd");
            var warn = daysLeft <= s.ExpiryDays;
            await EvaluateAsync(node, AlertRule.Expiry, current, warn, daysLeft, s.ExpiryDays, new RuleSpec(1, 1, AlertSeverity.Warning), now, null, ct);
            await EvaluateAsync(node, AlertRule.Expiry, current + ":critical", daysLeft <= 1, daysLeft, 1, new RuleSpec(1, 1, AlertSeverity.Critical), now, null, ct);
        }
        // Subjects for other dates (renewed / changed): resolve.
        foreach (var (key, st) in _states.Where(kv => kv.Key.NodeId == node.Id && kv.Key.Rule == AlertRule.Expiry).ToList())
        {
            if (current is not null && (key.Subject == current || key.Subject == current + ":critical")) continue;
            if (st.Phase == 2) await ResolveAsync(node, AlertRule.Expiry, st, now, silent: false, null, ct);
            else if (st.Phase != 0) { st.Phase = 0; st.Consecutive = 0; st.Dirty = true; }
        }
        await PersistDirtyStatesAsync(now, ct);
    }

    public void RemoveNode(int nodeId)
    {
        foreach (var key in _states.Keys.Where(k => k.NodeId == nodeId).ToList()) _states.TryRemove(key, out _);
    }

    private async Task BroadcastAsync(AlertEvent ev, CancellationToken ct)
    {
        try
        {
            await _adminHub.Clients.All.SendAsync(AdminHubMethods.Alert, new AdminAlertDto
            {
                Id = ev.Id, NodeId = ev.NodeId ?? 0, NodeName = ev.NodeName, Rule = (byte)ev.Rule, Status = (byte)ev.Status, Severity = (byte)ev.Severity,
                Title = ev.Title, Message = ev.Message, Ts = LiveSnapshotBuilder.UnixMs(ev.ResolvedAt ?? ev.StartedAt),
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "alert broadcast failed");
        }
    }

    private async Task PersistDirtyStatesAsync(DateTime now, CancellationToken ct)
    {
        var dirty = _states.Where(kv => kv.Value.Dirty).ToList();
        if (dirty.Count == 0) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var (key, st) in dirty)
        {
            var row = await db.AlertStates.FirstOrDefaultAsync(x => x.NodeId == key.NodeId && x.Rule == key.Rule && x.Subject == key.Subject, ct);
            if (row is null)
            {
                if (!Registry.All.Any(n => n.Id == key.NodeId)) { st.Dirty = false; continue; }
                row = new AlertState { NodeId = key.NodeId, Rule = key.Rule, Subject = key.Subject };
                db.AlertStates.Add(row);
            }
            row.State = st.Phase; row.Consecutive = st.Consecutive; row.ResolveCount = st.ResolveCount; row.FiringSince = st.FiringSince;
            row.LastNotifiedAt = st.LastNotifiedAt; row.CooldownUntil = st.CooldownUntil; row.LastValue = st.LastValue; row.OpenEventId = st.OpenEventId; row.UpdatedAt = now;
            st.Dirty = false;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Firing rules per node for API output.</summary>
    public IReadOnlyList<(int Rule, DateTime Since)> Firing(int nodeId) =>
        _states.Where(kv => kv.Key.NodeId == nodeId && kv.Value.Phase == 2).Select(kv => (kv.Key.Rule, kv.Value.FiringSince ?? DateTime.UtcNow)).ToList();
}
