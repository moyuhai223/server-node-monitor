using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Master.Alerting;
using SNM.Master.Api;
using SNM.Master.Api.Dto;
using SNM.Master.Auth;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Hubs;
using SNM.Master.Runtime;

namespace SNM.Master.Services;

/// <summary>Node CRUD with partial (PATCH) semantics, side effects on the live registry, metrics/traffic queries (docs/API.md 4).</summary>
public sealed class NodeService(
    IDbContextFactory<SnmDbContext> dbFactory, DbWriteLock writeLock, NodeRegistry registry, SettingsService settings, AlertEngine alerts,
    AgentConnectionTracker tracker, IHubContext<AgentHub> agentHub, ILogger<NodeService> logger)
{
    private static readonly Regex CountryCode = new("^[A-Za-z]{2}$", RegexOptions.Compiled);
    private static readonly int[] BillingCycles = [0, 1, 3, 6, 12, 24, 36];
    private static readonly string[] Currencies = ["USD", "CNY", "EUR"];

    // ------------------------------------------------------------------ read

    public async Task<PagedData<NodeDetailDto>> ListAsync(NodeListQuery q, CancellationToken ct)
    {
        var ips = await LoadIpsAsync(null, ct);
        IEnumerable<NodeRuntime> nodes = registry.All;
        if (q.Status is { } st) nodes = nodes.Where(n => n.Status == st);
        if (q.Enabled is { } en) nodes = nodes.Where(n => n.Meta.Enabled == en);
        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            var kw = q.Keyword.Trim();
            nodes = nodes.Where(n =>
                n.Meta.PublicName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || (n.Meta.AdminRemark?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false)
                || (n.Meta.Hostname?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false)
                || n.RemoteIp.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || (ips.TryGetValue(n.Id, out var list) && list.Any(ip => ip.Address.Contains(kw, StringComparison.OrdinalIgnoreCase))));
        }

        var items = nodes.Select(n => ToDetail(n, ips.GetValueOrDefault(n.Id) ?? [], includeNotes: false)).ToList();
        var desc = string.Equals(q.SortDir, "desc", StringComparison.OrdinalIgnoreCase);
        Func<NodeDetailDto, object?> key = (q.SortBy ?? "sortOrder") switch
        {
            "publicName" => d => d.PublicName,
            "status" => d => d.State.Status,
            "lastSeenAt" => d => d.State.LastSeenAt ?? DateTime.MinValue,
            "expiresAt" => d => d.Finance.ExpiresAt ?? DateOnly.MaxValue,
            "trafficPct" => d => d.Traffic.Pct ?? -1,
            _ => d => d.SortOrder,
        };
        items = (desc ? items.OrderByDescending(key).ThenBy(d => d.Id) : items.OrderBy(key).ThenBy(d => d.Id)).ToList();

        var total = items.Count;
        if (q.PageSize > 0) items = items.Skip((q.PageNo - 1) * q.PageSize).Take(q.PageSize).ToList();
        return new PagedData<NodeDetailDto> { PageData = items, Total = total, PageNo = q.PageNo, PageSize = q.PageSize };
    }

    public async Task<NodeDetailDto> GetAsync(int id, CancellationToken ct)
    {
        var node = registry.Get(id) ?? throw ApiException.NodeNotFound();
        var ips = await LoadIpsAsync(id, ct);
        return ToDetail(node, ips.GetValueOrDefault(id) ?? [], includeNotes: true);
    }

    private async Task<Dictionary<int, List<NodeIp>>> LoadIpsAsync(int? nodeId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.NodeIps.AsNoTracking();
        if (nodeId is { } id) query = query.Where(x => x.NodeId == id);
        var rows = await query.OrderBy(x => x.Family).ThenByDescending(x => x.IsPublic).ThenBy(x => x.Address).ToListAsync(ct);
        return rows.GroupBy(x => x.NodeId).ToDictionary(g => g.Key, g => g.ToList());
    }

    public NodeDetailDto ToDetail(NodeRuntime n, List<NodeIp> ips, bool includeNotes)
    {
        var meta = n.Meta;
        var now = DateTime.UtcNow;
        var live = n.Live;
        var tz = BillingPeriod.ResolveTimeZone(meta.TimeZoneId, settings.Snapshot.TimeZoneId);
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, settings.Snapshot.TimeZone));
        var billed = BillingPeriod.Billed(n.Traffic.PerRx, n.Traffic.PerTx, meta.TrafficCountMode);
        var dto = new NodeDetailDto
        {
            Id = meta.Id,
            PublicName = meta.PublicName,
            AdminRemark = meta.AdminRemark,
            Enabled = meta.Enabled,
            PublicVisible = meta.PublicVisible,
            SortOrder = meta.SortOrder,
            CountryCode = n.CountryCode,
            CountryCodeAuto = meta.CountryCodeAuto,
            CountryCodeOverride = meta.CountryCodeOverride,
            TimeZoneId = meta.TimeZoneId,
            IntervalMs = meta.IntervalMs,
            AgentKeyMasked = MaskKey(meta.AgentKey),
            KeyRotatedAt = meta.KeyRotatedAt,
            Hardware = new NodeHardwareDto
            {
                Hostname = meta.Hostname, Os = meta.Os, Kernel = meta.Kernel, Arch = meta.Arch, CpuModel = meta.CpuModel, CpuCores = meta.CpuCores,
                MemTotalMb = meta.MemTotalMb, SwapTotalMb = meta.SwapTotalMb, Disks = n.Disks.Select(NodeDiskDto.From).ToArray(),
                NetIfs = meta.NetIfs, Virt = meta.Virt, AgentVersion = meta.AgentVersion, ProtocolVersion = meta.ProtocolVersion, BootTimeUtc = n.BootTimeUtc,
            },
            Ips = ips.Select(ip => new NodeIpDto { Address = ip.Address, Family = ip.Family, IsPublic = ip.IsPublic, Source = ip.Source, FirstSeenAt = ip.FirstSeenAt, LastSeenAt = ip.LastSeenAt }).ToArray(),
            RemoteIp = n.RemoteIp.Length > 0 ? n.RemoteIp : meta.LastRemoteIp,
            State = new NodeStateDto
            {
                Status = n.Status, Connected = n.Connected, FirstSeenAt = meta.FirstSeenAt, LastSeenAt = n.LastSeenAt ?? meta.LastSeenAt,
                LastRegisterAt = meta.LastRegisterAt, StatusChangedAt = n.StatusChangedAt ?? meta.StatusChangedAt,
                UptimeSec = live?.UptimeSec ?? 0,
            },
            Live = live is null ? null : new NodeLiveDto
            {
                CpuPermille = live.Cpu, MemUsedMb = live.MemUsedMb, SwapUsedMb = live.SwapUsedMb, DiskUsedMb = live.DiskUsedMb.Select(d => (long)d).ToArray(),
                RxBps = (long)live.RxBps, TxBps = (long)live.TxBps, Load1 = live.Load1, Ts = live.Ts,
            },
            Traffic = new NodeTrafficDto
            {
                LimitBytes = meta.TrafficLimitBytes, CountMode = meta.TrafficCountMode, ResetDay = meta.TrafficResetDay,
                PeriodStart = n.Traffic.PeriodStart, PeriodEnd = n.Traffic.PeriodEnd, RxBytes = n.Traffic.PerRx, TxBytes = n.Traffic.PerTx,
                BilledBytes = billed, Pct = meta.TrafficLimitBytes > 0 ? Math.Round(billed * 100.0 / meta.TrafficLimitBytes, 1) : null, TimeZoneId = tz.Id,
            },
            Finance = new NodeFinanceDto
            {
                Vendor = meta.Vendor, Price = meta.Price, Currency = meta.Currency, BillingCycleMonths = meta.BillingCycleMonths, ExpiresAt = meta.ExpiresAt,
                DaysLeft = meta.ExpiresAt is { } exp ? exp.DayNumber - todayLocal.DayNumber : null, AutoRenew = meta.AutoRenew, RenewUrl = meta.RenewUrl,
            },
            Alerts = new NodeAlertsDto
            {
                AlertsEnabled = meta.AlertsEnabled, CpuAlertPct = meta.CpuAlertPct, TrafficAlertPct = meta.TrafficAlertPct, OfflineAlertSec = meta.OfflineAlertSec, DiskAlertPct = meta.DiskAlertPct,
                Firing = alerts.Firing(meta.Id).Select(f => new FiringDto { Rule = f.Rule, Since = f.Since }).ToArray(),
            },
            Notes = includeNotes ? meta.Notes : null,
            CreatedAt = meta.CreatedAt,
            UpdatedAt = meta.UpdatedAt,
        };
        return dto;
    }

    public static string MaskKey(string key) => key.Length >= 8 ? key[..5] + "****" + key[^4..] : "****";

    // ------------------------------------------------------------------ write

    public async Task<NodeDetailDto> CreateAsync(JsonElement body, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var entity = new Node
        {
            AgentKey = Tokens.NewAgentKey(),
            IntervalMs = settings.Snapshot.DefaultIntervalMs,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var v = new Validator();
        Apply(entity, body, v, isCreate: true);
        v.ThrowIfInvalid();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Nodes.AnyAsync(x => x.PublicName == entity.PublicName, ct)) throw ApiException.Business(409, ApiCodes.NodeNameExists, "节点名称已存在");
        if (entity.SortOrder == 0) entity.SortOrder = (await db.Nodes.MaxAsync(x => (int?)x.SortOrder, ct) ?? 0) + 10;
        db.Nodes.Add(entity);
        await db.SaveChangesAsync(ct);

        var node = registry.Upsert(entity);
        await registry.ReloadTrafficAsync(node, ct);
        registry.RaiseNodesChanged(entity.Id);
        logger.LogInformation("Node {NodeId} ({Name}) created", entity.Id, entity.PublicName);
        var dto = ToDetail(node, [], includeNotes: true);
        dto.AgentKey = entity.AgentKey;
        return dto;
    }

    public async Task<NodeDetailDto> UpdateAsync(int id, JsonElement body, CancellationToken ct)
    {
        var node = registry.Get(id) ?? throw ApiException.NodeNotFound();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Nodes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NodeNotFound();
        var before = (entity.IntervalMs, entity.Enabled, entity.ExpiresAt, entity.TrafficResetDay, entity.TimeZoneId, entity.TrafficCountMode, entity.TrafficLimitBytes);

        var v = new Validator();
        Apply(entity, body, v, isCreate: false);
        v.ThrowIfInvalid();
        if (await db.Nodes.AnyAsync(x => x.Id != id && x.PublicName == entity.PublicName, ct)) throw ApiException.Business(409, ApiCodes.NodeNameExists, "节点名称已存在");
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        registry.Upsert(entity);
        var now = DateTime.UtcNow;

        if (before.IntervalMs != entity.IntervalMs && node.Connected) await PushConfigureAsync(node, ct);
        if (before.Enabled && !entity.Enabled)
        {
            string? conn;
            lock (node.Sync)
            {
                conn = node.ConnectionId;
                if (node.Status == NodeStatus.Online) { node.Status = NodeStatus.Offline; node.StatusChangedAt = now; }
                node.StateDirty = true; node.Dirty = true;
            }
            tracker.Abort(conn);
        }
        if (before.ExpiresAt != entity.ExpiresAt) await alerts.EvaluateExpiryAsync(node, now, ct);
        if (before.TrafficResetDay != entity.TrafficResetDay || before.TimeZoneId != entity.TimeZoneId || before.TrafficCountMode != entity.TrafficCountMode || before.TrafficLimitBytes != entity.TrafficLimitBytes)
            await registry.ReloadTrafficAsync(node, ct);
        registry.RaiseNodesChanged(id);
        logger.LogInformation("Node {NodeId} ({Name}) updated", id, entity.PublicName);
        var ips = await LoadIpsAsync(id, ct);
        return ToDetail(node, ips.GetValueOrDefault(id) ?? [], includeNotes: true);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var node = registry.Get(id) ?? throw ApiException.NodeNotFound();
        using var _ = await writeLock.AcquireAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Metrics1m.Where(x => x.NodeId == id).ExecuteDeleteAsync(ct);
        await db.Metrics1h.Where(x => x.NodeId == id).ExecuteDeleteAsync(ct);
        await db.Metrics1d.Where(x => x.NodeId == id).ExecuteDeleteAsync(ct);
        await db.TrafficDaily.Where(x => x.NodeId == id).ExecuteDeleteAsync(ct);
        await db.TrafficMonthly.Where(x => x.NodeId == id).ExecuteDeleteAsync(ct);
        await db.Nodes.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);

        registry.Remove(id);
        string? conn;
        lock (node.Sync) { conn = node.ConnectionId; }
        tracker.Abort(conn);
        alerts.RemoveNode(id);
        registry.RaiseNodesChanged(id);
        logger.LogWarning("Node {NodeId} ({Name}) deleted", id, node.Meta.PublicName);
    }

    public async Task<(string Key, DateTime RotatedAt)> RotateKeyAsync(int id, CancellationToken ct)
    {
        var node = registry.Get(id) ?? throw ApiException.NodeNotFound();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Nodes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NodeNotFound();
        var now = DateTime.UtcNow;
        entity.AgentKey = Tokens.NewAgentKey();
        entity.KeyRotatedAt = now;
        entity.UpdatedAt = now;
        await db.InstallTokens.Where(x => x.NodeId == id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        registry.Upsert(entity);
        string? conn;
        lock (node.Sync) { conn = node.ConnectionId; }
        tracker.Abort(conn);
        logger.LogWarning("Node {NodeId} ({Name}) agent key rotated", id, entity.PublicName);
        return (entity.AgentKey, now);
    }

    public string RevealKey(int id, string? user, string? ip)
    {
        var node = registry.Get(id) ?? throw ApiException.NodeNotFound();
        logger.LogWarning("Agent key of node {NodeId} ({Name}) revealed by {User} from {Ip}", id, node.Meta.PublicName, user, ip);
        return node.Meta.AgentKey;
    }

    public async Task ReorderAsync(int[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Nodes.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var order = 10;
        foreach (var id in ids)
        {
            var row = rows.FirstOrDefault(x => x.Id == id);
            if (row is null) continue;
            row.SortOrder = order;
            row.UpdatedAt = DateTime.UtcNow;
            order += 10;
        }
        await db.SaveChangesAsync(ct);
        foreach (var row in rows) registry.Upsert(row);
        registry.RaiseNodesChanged(rows.Select(r => r.Id).ToArray());
    }

    public async Task PushConfigureAsync(NodeRuntime node, CancellationToken ct)
    {
        string? conn;
        lock (node.Sync) { conn = node.Connected ? node.ConnectionId : null; }
        if (conn is null) return;
        var cfg = new AgentConfigDto
        {
            IntervalMs = (ushort)Math.Clamp(node.Meta.IntervalMs, ProtocolConstants.MinIntervalMs, ProtocolConstants.MaxIntervalMs),
            StatusIntervalSec = (ushort)Math.Clamp(settings.Snapshot.StatusIntervalSec, ProtocolConstants.MinStatusIntervalSec, ProtocolConstants.MaxStatusIntervalSec),
        };
        await agentHub.Clients.Client(conn).SendAsync(AgentHubMethods.Configure, cfg, ct);
        logger.LogInformation("Pushed configure (interval {Interval} ms) to node {NodeId}", cfg.IntervalMs, node.Id);
    }

    public async Task PushConfigureAllAsync(CancellationToken ct)
    {
        foreach (var node in registry.All)
        {
            try { await PushConfigureAsync(node, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "configure push failed for node {NodeId}", node.Id); }
        }
    }

    // ------------------------------------------------------------------ metrics / traffic

    public async Task<MetricsResponseDto> MetricsAsync(int id, string? range, long? from, long? to, CancellationToken ct)
    {
        _ = registry.Get(id) ?? throw ApiException.NodeNotFound();
        range = (range ?? "24h").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (step, span) = range switch
        {
            "24h" => (60, 86400L),
            "7d" => (3600, 7 * 86400L),
            "30d" => (86400, 30 * 86400L),
            _ => throw ApiException.BadRequest("range 必须是 24h / 7d / 30d 之一"),
        };
        var fromTs = Math.Max(from ?? 0, (now - span) / step * step);
        var toTs = Math.Min(to ?? long.MaxValue, now / step * step + step);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        MetricPointDto[] points = range switch
        {
            "24h" => await QueryAsync(db.Metrics1m, id, fromTs, toTs, ct),
            "7d" => await QueryAsync(db.Metrics1h, id, fromTs, toTs, ct),
            _ => await QueryAsync(db.Metrics1d, id, fromTs, toTs, ct),
        };
        return new MetricsResponseDto { Range = range, StepSec = step, FromTs = fromTs, ToTs = toTs, Points = points };
    }

    private static Task<MetricPointDto[]> QueryAsync<T>(DbSet<T> set, int id, long from, long to, CancellationToken ct) where T : MetricBucket =>
        set.AsNoTracking().Where(x => x.NodeId == id && x.Ts >= from && x.Ts < to).OrderBy(x => x.Ts).Select(x => new MetricPointDto
        {
            Ts = x.Ts, Samples = x.Samples, CpuAvg = x.CpuAvg, CpuMax = x.CpuMax, MemUsedAvgMb = x.MemUsedAvgMb, MemUsedMaxMb = x.MemUsedMaxMb, SwapUsedAvgMb = x.SwapUsedAvgMb,
            DiskUsedMb = x.DiskUsedMb, DiskTotalMb = x.DiskTotalMb, RxBpsAvg = x.RxBpsAvg, RxBpsMax = x.RxBpsMax, TxBpsAvg = x.TxBpsAvg, TxBpsMax = x.TxBpsMax,
            RxBytes = x.RxBytes, TxBytes = x.TxBytes, Load1Avg = x.Load1Avg, Load1Max = x.Load1Max,
        }).ToArrayAsync(ct);

    public async Task<TrafficResponseDto> TrafficAsync(int id, int periods, CancellationToken ct)
    {
        var node = registry.Get(id) ?? throw ApiException.NodeNotFound();
        periods = Math.Clamp(periods, 1, 36);
        var meta = node.Meta;
        DateOnly start, end; long rx, tx;
        lock (node.Sync) { start = node.Traffic.PeriodStart; end = node.Traffic.PeriodEnd; rx = node.Traffic.PerRx; tx = node.Traffic.PerTx; }
        var billed = BillingPeriod.Billed(rx, tx, meta.TrafficCountMode);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var daily = await db.TrafficDaily.AsNoTracking().Where(x => x.NodeId == id && x.Date >= start && x.Date < end).OrderBy(x => x.Date)
            .Select(x => new TrafficDailyDto { Date = x.Date, RxBytes = x.RxBytes, TxBytes = x.TxBytes }).ToArrayAsync(ct);
        // today's row may lag the in-memory counters by up to a minute; patch it from memory
        DateOnly day; long dayRx, dayTx;
        lock (node.Sync) { day = node.Traffic.Day; dayRx = node.Traffic.DayRx; dayTx = node.Traffic.DayTx; }
        if (day != default && day >= start && day < end)
        {
            var existing = daily.FirstOrDefault(d => d.Date == day);
            if (existing is null) daily = daily.Append(new TrafficDailyDto { Date = day, RxBytes = dayRx, TxBytes = dayTx }).OrderBy(d => d.Date).ToArray();
            else { existing.RxBytes = dayRx; existing.TxBytes = dayTx; }
        }
        var history = await db.TrafficMonthly.AsNoTracking().Where(x => x.NodeId == id && x.PeriodStart < start).OrderByDescending(x => x.PeriodStart).Take(periods)
            .Select(x => new TrafficHistoryDto { PeriodStart = x.PeriodStart, PeriodEnd = x.PeriodEnd, RxBytes = x.RxBytes, TxBytes = x.TxBytes, BilledBytes = x.BilledBytes, LimitBytes = x.LimitBytes, Closed = x.Closed })
            .ToArrayAsync(ct);
        var tz = BillingPeriod.ResolveTimeZone(meta.TimeZoneId, settings.Snapshot.TimeZoneId);
        return new TrafficResponseDto
        {
            Current = new TrafficCurrentDto
            {
                LimitBytes = meta.TrafficLimitBytes, CountMode = meta.TrafficCountMode, ResetDay = meta.TrafficResetDay, PeriodStart = start, PeriodEnd = end,
                RxBytes = rx, TxBytes = tx, BilledBytes = billed, Pct = meta.TrafficLimitBytes > 0 ? Math.Round(billed * 100.0 / meta.TrafficLimitBytes, 1) : null,
                TimeZoneId = tz.Id, Daily = daily,
            },
            History = history,
        };
    }

    public async Task<TrafficResponseDto> ResetTrafficAsync(int id, CancellationToken ct)
    {
        var node = registry.Get(id) ?? throw ApiException.NodeNotFound();
        lock (node.Sync)
        {
            node.Traffic.ResetPeriodCounters();
            node.Traffic.PeriodChanged = true;   // alert engine silently closes traffic alerts
            node.Dirty = true;
        }
        logger.LogWarning("Traffic counters of node {NodeId} ({Name}) reset for the current period", id, node.Meta.PublicName);
        return await TrafficAsync(id, 12, ct);
    }

    // ------------------------------------------------------------------ apply / validate

    private void Apply(Node e, JsonElement body, Validator v, bool isCreate)
    {
        if (body.ValueKind != JsonValueKind.Object) { v.Add("body", "请求体必须是 JSON 对象"); return; }

        if (Try(body, "publicName", out var el))
        {
            var s = Str(el, "publicName", v, 1, 64, allowNull: false);
            if (s is not null) e.PublicName = s;
        }
        else if (isCreate) v.Add("publicName", "不能为空");

        if (Try(body, "adminRemark", out el)) e.AdminRemark = Str(el, "adminRemark", v, 0, 256, allowNull: true);
        if (Try(body, "enabled", out el)) e.Enabled = Bool(el, "enabled", v) ?? e.Enabled;
        if (Try(body, "publicVisible", out el)) e.PublicVisible = Bool(el, "publicVisible", v) ?? e.PublicVisible;
        if (Try(body, "sortOrder", out el)) e.SortOrder = Int(el, "sortOrder", v, int.MinValue, int.MaxValue) ?? e.SortOrder;
        if (Try(body, "countryCodeOverride", out el))
        {
            var s = Str(el, "countryCodeOverride", v, 2, 2, allowNull: true);
            if (s is not null && !CountryCode.IsMatch(s)) v.Add("countryCodeOverride", "必须是两位字母国家码");
            else e.CountryCodeOverride = s?.ToUpperInvariant();
        }
        if (Try(body, "timeZoneId", out el))
        {
            var s = Str(el, "timeZoneId", v, 1, 64, allowNull: true);
            if (s is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(s, out _)) v.Add("timeZoneId", "无效的时区标识");
            else e.TimeZoneId = s;
        }
        if (Try(body, "intervalMs", out el)) e.IntervalMs = Int(el, "intervalMs", v, ProtocolConstants.MinIntervalMs, ProtocolConstants.MaxIntervalMs) ?? e.IntervalMs;
        if (Try(body, "notes", out el)) e.Notes = Str(el, "notes", v, 0, 2000, allowNull: true);

        if (Try(body, "traffic", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            if (Try(t, "limitBytes", out el)) e.TrafficLimitBytes = Long(el, "traffic.limitBytes", v, 0, long.MaxValue) ?? e.TrafficLimitBytes;
            if (Try(t, "countMode", out el)) e.TrafficCountMode = Int(el, "traffic.countMode", v, 0, 3) ?? e.TrafficCountMode;
            if (Try(t, "resetDay", out el)) e.TrafficResetDay = Int(el, "traffic.resetDay", v, 1, 31) ?? e.TrafficResetDay;
        }

        if (Try(body, "finance", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            if (Try(f, "vendor", out el)) e.Vendor = Str(el, "finance.vendor", v, 0, 64, allowNull: true);
            if (Try(f, "price", out el)) e.Price = Price(el, v);
            if (Try(f, "currency", out el))
            {
                var s = Str(el, "finance.currency", v, 3, 3, allowNull: true)?.ToUpperInvariant();
                if (s is not null && !Currencies.Contains(s)) v.Add("finance.currency", "须为 USD / CNY / EUR 之一");
                else e.Currency = s;
            }
            if (Try(f, "billingCycleMonths", out el))
            {
                var n = Int(el, "finance.billingCycleMonths", v, 0, 36);
                if (n is { } m && !BillingCycles.Contains(m)) v.Add("finance.billingCycleMonths", "须为 0/1/3/6/12/24/36 之一");
                else if (n is { } ok) e.BillingCycleMonths = ok;
            }
            if (Try(f, "expiresAt", out el)) e.ExpiresAt = Date(el, "finance.expiresAt", v);
            if (Try(f, "autoRenew", out el)) e.AutoRenew = Bool(el, "finance.autoRenew", v) ?? e.AutoRenew;
            if (Try(f, "renewUrl", out el))
            {
                var s = Str(el, "finance.renewUrl", v, 0, 512, allowNull: true);
                if (!string.IsNullOrEmpty(s) && !(Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")) v.Add("finance.renewUrl", "须为 http(s) 地址");
                else e.RenewUrl = string.IsNullOrEmpty(s) ? null : s;
            }
        }

        if (Try(body, "alerts", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            if (Try(a, "alertsEnabled", out el)) e.AlertsEnabled = Bool(el, "alerts.alertsEnabled", v) ?? e.AlertsEnabled;
            if (Try(a, "cpuAlertPct", out el)) e.CpuAlertPct = NullableInt(el, "alerts.cpuAlertPct", v, 50, 100, e.CpuAlertPct);
            if (Try(a, "trafficAlertPct", out el)) e.TrafficAlertPct = NullableInt(el, "alerts.trafficAlertPct", v, 50, 99, e.TrafficAlertPct);
            if (Try(a, "offlineAlertSec", out el)) e.OfflineAlertSec = NullableInt(el, "alerts.offlineAlertSec", v, 10, 600, e.OfflineAlertSec);
            if (Try(a, "diskAlertPct", out el)) e.DiskAlertPct = NullableInt(el, "alerts.diskAlertPct", v, 50, 100, e.DiskAlertPct);
        }
    }

    private static bool Try(JsonElement obj, string name, out JsonElement value) => obj.TryGetProperty(name, out value);

    private static string? Str(JsonElement el, string field, Validator v, int min, int max, bool allowNull)
    {
        if (el.ValueKind == JsonValueKind.Null)
        {
            if (!allowNull) v.Add(field, "不能为空");
            return null;
        }
        if (el.ValueKind != JsonValueKind.String) { v.Add(field, "必须是字符串"); return null; }
        var s = el.GetString()!.Trim();
        if (s.Length == 0 && allowNull) return null;
        if (s.Length < min || s.Length > max) { v.Add(field, $"长度必须在 {min}–{max} 之间"); return null; }
        return s;
    }

    private static bool? Bool(JsonElement el, string field, Validator v)
    {
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        v.Add(field, "必须是布尔值");
        return null;
    }

    private static int? Int(JsonElement el, string field, Validator v, int min, int max)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
        {
            if (n < min || n > max) { v.Add(field, $"必须在 {min}–{max} 之间"); return null; }
            return n;
        }
        v.Add(field, "必须是整数");
        return null;
    }

    private static long? Long(JsonElement el, string field, Validator v, long min, long max)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
        {
            if (n < min || n > max) { v.Add(field, $"必须在 {min}–{max} 之间"); return null; }
            return n;
        }
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s >= min && s <= max) return s;
        v.Add(field, "必须是整数");
        return null;
    }

    private static int? NullableInt(JsonElement el, string field, Validator v, int min, int max, int? current)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;
        return Int(el, field, v, min, max) ?? current;
    }

    private static decimal? Price(JsonElement el, Validator v)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;
        decimal d;
        if (el.ValueKind == JsonValueKind.Number) d = el.GetDecimal();
        else if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p)) d = p;
        else { v.Add("finance.price", "必须是十进制数字字符串"); return null; }
        if (d < 0 || d > 1_000_000_000m) { v.Add("finance.price", "必须 ≥ 0"); return null; }
        if (decimal.Round(d, 2) != d) { v.Add("finance.price", "最多 2 位小数"); return null; }
        return d;
    }

    private static DateOnly? Date(JsonElement el, string field, Validator v)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(el.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        v.Add(field, "格式须为 yyyy-MM-dd");
        return null;
    }
}
