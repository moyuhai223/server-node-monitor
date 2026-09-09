using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Runtime;

namespace SNM.Master.Services;

/// <summary>Tracks live agent hub connections so a newer connection of the same node can abort the older one.</summary>
public sealed class AgentConnectionTracker
{
    private readonly ConcurrentDictionary<string, HubCallerContext> _contexts = new(StringComparer.Ordinal);

    public void Add(HubCallerContext ctx) => _contexts[ctx.ConnectionId] = ctx;
    public void Remove(string connectionId) => _contexts.TryRemove(connectionId, out _);
    public int Count => _contexts.Count;

    public void Abort(string? connectionId)
    {
        if (connectionId is null) return;
        if (_contexts.TryRemove(connectionId, out var ctx))
        {
            try { ctx.Abort(); } catch { /* connection already gone */ }
        }
    }
}

public enum DropReason { None, StaleConnection, Unregistered, Sequence }

/// <summary>Ingestion rules R1-R10 from docs/PROTOCOL.md 4.6. Heartbeats are memory-only; register/status touch the database.</summary>
public sealed class NodeIngestService(
    NodeRegistry registry, IDbContextFactory<SnmDbContext> dbFactory, GeoIpService geoIp, SettingsService settings,
    AgentConnectionTracker tracker, ILogger<NodeIngestService> logger)
{
    public static readonly int[] SupportedProtocolVersions = [ProtocolConstants.ProtocolVersion];

    private static string Clip(string? s, int max = ProtocolConstants.MaxStringChars) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];

    private static DiskInfoDto[] CleanDisks(DiskInfoDto[]? disks)
    {
        if (disks is null) return [];
        return disks.Take(ProtocolConstants.MaxDisks)
            .Select(d => new DiskInfoDto { Mount = Clip(d.Mount, 128), Fs = Clip(d.Fs, 32), TotalMb = d.TotalMb })
            .ToArray();
    }

    private static List<IPAddress> CleanIps(string[]? ips)
    {
        var result = new List<IPAddress>();
        if (ips is null) return result;
        foreach (var s in ips.Take(ProtocolConstants.MaxIps))
        {
            if (IPAddress.TryParse(s, out var ip) && !result.Contains(ip)) result.Add(ip);
        }
        return result;
    }

    private static bool DisksEqual(DiskInfoDto[] a, DiskInfoDto[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].Mount != b[i].Mount || a[i].Fs != b[i].Fs || a[i].TotalMb != b[i].TotalMb) return false;
        }
        return true;
    }

    public async Task<AgentConfigDto> RegisterAsync(NodeRuntime node, RegisterDto dto, HubCallerContext ctx, IPAddress? remoteIp, DateTime now, CancellationToken ct)
    {
        if (!SupportedProtocolVersions.Contains(dto.ProtocolVersion))
        {
            throw new HubException($"unsupported protocol version {dto.ProtocolVersion}; server supports {string.Join(",", SupportedProtocolVersions)}");
        }

        var disks = CleanDisks(dto.Disks);
        var netIfs = Clip(dto.NetIfs);
        string? oldConnection = null;
        bool netIfsChanged;

        lock (node.Sync)
        {
            if (node.ConnectionId is not null && node.ConnectionId != ctx.ConnectionId) oldConnection = node.ConnectionId;
            node.ConnectionId = ctx.ConnectionId;
            node.Connected = true;
            node.Registered = true;
            node.WarnedUnregistered = false;
            node.LastSeq = 0;
            node.InventoryMismatch = false;
            node.RemoteIp = remoteIp?.ToString() ?? "";
            node.BootTimeUtc = now.AddSeconds(-dto.UptimeSec);
            node.Disks = disks;
            netIfsChanged = !string.Equals(node.Meta.NetIfs ?? "", netIfs, StringComparison.Ordinal) && node.Meta.LastRegisterAt is not null;
            if (netIfsChanged) node.Traffic.ResetBaseline();
            node.MarkSeen(now);
        }

        if (oldConnection is not null)
        {
            logger.LogInformation("Node {NodeId} re-registered on {New}; aborting stale connection {Old}", node.Id, ctx.ConnectionId, oldConnection);
            tracker.Abort(oldConnection);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Nodes.FirstOrDefaultAsync(x => x.Id == node.Id, ct) ?? throw new HubException("node no longer exists");
        entity.AgentVersion = Clip(dto.AgentVersion, 32);
        entity.ProtocolVersion = dto.ProtocolVersion;
        entity.Hostname = Clip(dto.Hostname, 64);
        entity.Os = Clip(dto.Os, 128);
        entity.Kernel = Clip(dto.Kernel, 64);
        entity.Arch = Clip(dto.Arch, 16);
        entity.CpuModel = Clip(dto.CpuModel, 128);
        entity.CpuCores = dto.CpuCores;
        entity.MemTotalMb = dto.MemTotalMb;
        entity.SwapTotalMb = dto.SwapTotalMb;
        entity.DisksJson = NodeRuntime.SerializeDisks(disks);
        entity.NetIfs = netIfs;
        entity.Virt = Clip(dto.Virt, 32);
        entity.BootTimeUtc = node.BootTimeUtc;
        entity.FirstSeenAt ??= now;
        entity.LastRegisterAt = now;
        entity.LastSeenAt = now;
        entity.LastRemoteIp = node.RemoteIp.Length > 0 ? node.RemoteIp : entity.LastRemoteIp;
        entity.Status = NodeStatus.Online;
        entity.StatusChangedAt = node.StatusChangedAt;
        entity.UpdatedAt = now;

        var ips = CleanIps(dto.Ips);
        await MergeIpsAsync(db, entity, ips, remoteIp, now, fullReplace: true, ct);
        ApplyGeo(entity, remoteIp);
        await db.SaveChangesAsync(ct);

        registry.Upsert(entity);
        logger.LogInformation("Node {NodeId} ({Name}) registered from {Ip}: agent {Agent}, {Os}, {Cores} cores, {Disks} disks, ifs={NetIfs}",
            node.Id, entity.PublicName, node.RemoteIp, entity.AgentVersion, entity.Os, entity.CpuCores, disks.Length, netIfs);
        registry.RaiseNodesChanged(node.Id);

        return new AgentConfigDto
        {
            IntervalMs = (ushort)Math.Clamp(entity.IntervalMs, ProtocolConstants.MinIntervalMs, ProtocolConstants.MaxIntervalMs),
            StatusIntervalSec = (ushort)Math.Clamp(settings.Snapshot.StatusIntervalSec, ProtocolConstants.MinStatusIntervalSec, ProtocolConstants.MaxStatusIntervalSec),
        };
    }

    /// <summary>R2-R6: memory only. Returns the reason when the heartbeat was dropped.</summary>
    public DropReason OnHeartbeat(NodeRuntime node, HeartbeatDto hb, string connectionId, DateTime now)
    {
        lock (node.Sync)
        {
            if (node.ConnectionId != connectionId) return DropReason.StaleConnection;
            if (!node.Registered) return DropReason.Unregistered;
            if (hb.Seq <= node.LastSeq) return DropReason.Sequence;
            node.LastSeq = hb.Seq;

            var meta = node.Meta;
            var cpu = Math.Min(hb.Cpu, ProtocolConstants.CpuPermilleMax);
            var disks = node.Disks;
            uint[] diskUsed;
            if (hb.DiskUsedMb is not null && hb.DiskUsedMb.Length == disks.Length)
            {
                diskUsed = hb.DiskUsedMb;
                node.InventoryMismatch = false;
            }
            else
            {
                node.InventoryMismatch = disks.Length > 0;
                diskUsed = node.Live?.DiskUsedMb ?? new uint[disks.Length];
            }
            long diskUsedSum = 0;
            foreach (var d in diskUsed) diskUsedSum += d;
            var diskTotal = node.DiskTotalMb();

            var tz = BillingPeriod.ResolveTimeZone(meta.TimeZoneId, settings.Snapshot.TimeZoneId);
            var today = BillingPeriod.LocalToday(now, tz);
            var traffic = TrafficEngine.OnHeartbeat(node.Traffic, hb, now, connectionId, node.BootTimeUtc, today, meta.TrafficResetDay,
                msg => logger.LogWarning("Node {NodeId}: {Message}", node.Id, msg));

            node.MarkSeen(now);
            var uptime = node.BootTimeUtc is { } boot ? (uint)Math.Clamp((now - boot).TotalSeconds, 0, uint.MaxValue) : 0u;
            node.Live = new LiveSample(cpu, hb.MemUsedMb, hb.SwapUsedMb, diskUsed, (ulong)traffic.RxBps, (ulong)traffic.TxBps, hb.Load1, uptime, now);
            node.History.Add(new LivePoint(LiveSnapshotBuilder.UnixMs(now), cpu, LiveSnapshotBuilder.Permille(hb.MemUsedMb, meta.MemTotalMb), (ulong)traffic.RxBps, (ulong)traffic.TxBps));

            var bucket = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds() / 60 * 60;
            if (node.Acc is null || node.Acc.BucketTs != bucket)
            {
                if (node.Acc is { Samples: > 0 } done) node.FlushQueue.Enqueue(done.ToRow(node.Id));
                node.Acc = new MinuteAccumulator(bucket);
            }
            node.Acc.Add(hb, diskUsedSum, diskTotal, traffic.RxBps, traffic.TxBps, traffic.DRx, traffic.DTx);
            node.Dirty = true;
            return DropReason.None;
        }
    }

    public async Task<DropReason> StatusAsync(NodeRuntime node, StatusReportDto dto, string connectionId, DateTime now, CancellationToken ct)
    {
        var disks = CleanDisks(dto.Disks);
        var netIfs = Clip(dto.NetIfs);
        bool disksChanged, netIfsChanged, rebooted;
        DateTime? bootTime;

        lock (node.Sync)
        {
            if (node.ConnectionId != connectionId) return DropReason.StaleConnection;
            if (!node.Registered) return DropReason.Unregistered;
            bootTime = now.AddSeconds(-dto.UptimeSec);
            rebooted = node.BootTimeUtc is { } prev && Math.Abs((bootTime.Value - prev).TotalSeconds) > 120;
            node.BootTimeUtc = bootTime;
            disksChanged = dto.Disks is not null && !DisksEqual(node.Disks, disks);
            if (dto.Disks is not null) { node.Disks = disks; node.InventoryMismatch = false; }
            netIfsChanged = !string.Equals(node.Meta.NetIfs ?? "", netIfs, StringComparison.Ordinal);
            if (netIfsChanged) node.Traffic.ResetBaseline();
            node.ProcCount = dto.ProcCount;
            node.MarkSeen(now);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Nodes.FirstOrDefaultAsync(x => x.Id == node.Id, ct);
        if (entity is null) return DropReason.StaleConnection;
        if (dto.Disks is not null) entity.DisksJson = NodeRuntime.SerializeDisks(disks);
        entity.NetIfs = netIfs;
        entity.BootTimeUtc = bootTime;
        if (dto.MemTotalMb > 0) entity.MemTotalMb = dto.MemTotalMb;
        entity.LastSeenAt = now;
        entity.Status = NodeStatus.Online;
        entity.UpdatedAt = now;
        var ipsChanged = await MergeIpsAsync(db, entity, CleanIps(dto.Ips), null, now, fullReplace: dto.Ips is not null, ct);
        await db.SaveChangesAsync(ct);
        registry.Upsert(entity);

        if (rebooted) logger.LogInformation("Node {NodeId} reports a reboot (boot time {Boot:u})", node.Id, bootTime);
        if (disksChanged || ipsChanged || netIfsChanged) registry.RaiseNodesChanged(node.Id);
        return DropReason.None;
    }

    public void OnDisconnected(NodeRuntime node, string connectionId, Exception? exception)
    {
        lock (node.Sync)
        {
            if (node.ConnectionId != connectionId) return;
            node.ConnectionId = null;
            node.Connected = false;
            node.Registered = false;
            node.StateDirty = true;
            node.Dirty = true;
        }
        logger.LogInformation("Node {NodeId} disconnected ({Reason})", node.Id, exception?.Message ?? "clean");
    }

    /// <summary>R8: agent-reported set + server-captured remote IP, upserted into NodeIps. Returns true when the set changed.</summary>
    private async Task<bool> MergeIpsAsync(SnmDbContext db, Node entity, List<IPAddress> agentIps, IPAddress? remoteIp, DateTime now, bool fullReplace, CancellationToken ct)
    {
        var rows = await db.NodeIps.Where(x => x.NodeId == entity.Id).ToListAsync(ct);
        var byAddr = rows.ToDictionary(x => x.Address, StringComparer.Ordinal);
        var changed = false;

        void Upsert(IPAddress ip, int sourceBit)
        {
            var addr = Normalize(ip);
            if (byAddr.TryGetValue(addr, out var row))
            {
                if ((row.Source & sourceBit) == 0) { row.Source |= sourceBit; changed = true; }
                row.LastSeenAt = now;
            }
            else
            {
                row = new NodeIp
                {
                    NodeId = entity.Id, Address = addr, Family = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 6 : 4,
                    IsPublic = GeoIpService.IsPublic(ip), Source = sourceBit, FirstSeenAt = now, LastSeenAt = now,
                };
                db.NodeIps.Add(row);
                byAddr[addr] = row;
                changed = true;
            }
        }

        foreach (var ip in agentIps) Upsert(ip, 1);
        if (remoteIp is not null) Upsert(remoteIp, 2);

        if (fullReplace)
        {
            var reported = agentIps.Select(Normalize).ToHashSet(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if ((row.Source & 1) == 0 || reported.Contains(row.Address)) continue;
                if (row.Source == 1) { db.NodeIps.Remove(row); changed = true; }
                else { row.Source &= ~1; changed = true; }
            }
        }
        return changed;
    }

    public static string Normalize(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) ip = new IPAddress(ip.GetAddressBytes());
        return ip.ToString();
    }

    private void ApplyGeo(Node entity, IPAddress? remoteIp)
    {
        if (!settings.Snapshot.GeoIpEnabled || !geoIp.Ready) return;
        var cc = geoIp.Lookup(remoteIp);
        if (cc is not null) entity.CountryCodeAuto = cc;
    }
}
