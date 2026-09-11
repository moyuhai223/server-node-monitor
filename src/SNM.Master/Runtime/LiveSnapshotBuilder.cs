using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Master.Services;

namespace SNM.Master.Runtime;

/// <summary>Builds the browser-facing DTOs from NodeRuntime (docs/PROTOCOL.md 5-6). The public builder is a strict whitelist.</summary>
public sealed class LiveSnapshotBuilder(NodeRegistry registry, SettingsService settings)
{
    public static long UnixMs(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public static ushort Permille(double used, double total) => total <= 0 ? (ushort)0 : (ushort)Math.Clamp(Math.Round(used * 1000 / total), 0, 1000);

    public bool IsPublicVisible(NodeRuntime n) => n.Meta.Enabled && n.Meta.PublicVisible;

    public IEnumerable<NodeRuntime> PublicNodes() => registry.All.Where(IsPublicVisible).OrderBy(n => n.Meta.SortOrder).ThenBy(n => n.Meta.Id);

    public PublicSiteDto Site()
    {
        var s = settings.Snapshot;
        return new PublicSiteDto
        {
            Title = s.PublicTitle, Subtitle = s.PublicSubtitle, ShowSpecs = s.ShowSpecs, ShowTraffic = s.ShowTraffic, OfflineTimeoutSec = s.OfflineTimeoutSec,
            Theme = s.Theme, ThemeOptions = s.ThemeOptions,
        };
    }

    public PublicNodeLiveDto PublicLive(NodeRuntime n)
    {
        var live = n.Live;
        var meta = n.Meta;
        var diskTotal = n.DiskTotalMb();
        long diskUsed = 0;
        if (live is not null) foreach (var d in live.DiskUsedMb) diskUsed += d;
        var billed = BillingPeriod.Billed(n.Traffic.PerRx, n.Traffic.PerTx, meta.TrafficCountMode);
        return new PublicNodeLiveDto
        {
            Id = n.Id,
            Status = n.Status,
            Cpu = live?.Cpu ?? 0,
            Mem = live is null ? (ushort)0 : Permille(live.MemUsedMb, meta.MemTotalMb),
            Disk = live is null ? (ushort)0 : Permille(diskUsed, diskTotal),
            RxBps = live?.RxBps ?? 0,
            TxBps = live?.TxBps ?? 0,
            UptimeSec = live?.UptimeSec ?? 0,
            TrafficUsedBytes = (ulong)Math.Max(billed, 0),
            Ts = live is null ? 0 : UnixMs(live.Ts),
        };
    }

    public PublicHistoryDto PublicHistory(NodeRuntime n)
    {
        var pts = n.History.Snapshot();
        return new PublicHistoryDto
        {
            Cpu = pts.Select(p => p.Cpu).ToArray(),
            Mem = pts.Select(p => p.MemPermille).ToArray(),
            Rx = pts.Select(p => p.RxBps).ToArray(),
            Tx = pts.Select(p => p.TxBps).ToArray(),
        };
    }

    public PublicNodeDto PublicNode(NodeRuntime n, bool withHistory)
    {
        var meta = n.Meta;
        return new PublicNodeDto
        {
            Id = n.Id,
            Name = meta.PublicName,
            Cc = n.CountryCode,
            Order = meta.SortOrder,
            Cores = (ushort)Math.Clamp(meta.CpuCores, 0, ushort.MaxValue),
            MemTotalMb = (uint)Math.Clamp(meta.MemTotalMb, 0, uint.MaxValue),
            DiskTotalMb = (ulong)Math.Max(n.DiskTotalMb(), 0),
            TrafficLimitBytes = (ulong)Math.Max(meta.TrafficLimitBytes, 0),
            Live = PublicLive(n),
            Hist = withHistory ? PublicHistory(n) : null,
        };
    }

    public PublicSnapshotDto PublicSnapshot(DateTime now) => new()
    {
        ServerTs = UnixMs(now),
        Site = Site(),
        Nodes = PublicNodes().Select(n => PublicNode(n, withHistory: true)).ToArray(),
    };

    public AdminNodeLiveDto AdminLive(NodeRuntime n)
    {
        var live = n.Live;
        var meta = n.Meta;
        return new AdminNodeLiveDto
        {
            Id = n.Id,
            Status = n.Status,
            Connected = n.Connected,
            Cpu = live?.Cpu ?? 0,
            MemUsedMb = live?.MemUsedMb ?? 0,
            SwapUsedMb = live?.SwapUsedMb ?? 0,
            DiskUsedMb = live is null ? [] : live.DiskUsedMb.Select(d => (ulong)d).ToArray(),
            RxBps = live?.RxBps ?? 0,
            TxBps = live?.TxBps ?? 0,
            Load1 = live?.Load1 ?? 0,
            UptimeSec = live?.UptimeSec ?? 0,
            LastSeenTs = n.LastSeenAt is { } ls ? UnixMs(ls) : 0,
            RemoteIp = n.RemoteIp,
            TrafficUsedBytes = (ulong)Math.Max(BillingPeriod.Billed(n.Traffic.PerRx, n.Traffic.PerTx, meta.TrafficCountMode), 0),
            TrafficRxBytes = (ulong)Math.Max(n.Traffic.PerRx, 0),
            TrafficTxBytes = (ulong)Math.Max(n.Traffic.PerTx, 0),
            Seq = n.LastSeq,
        };
    }

    public AdminSnapshotDto AdminSnapshot(DateTime now) => new()
    {
        ServerTs = UnixMs(now),
        Nodes = registry.All.OrderBy(n => n.Meta.SortOrder).ThenBy(n => n.Id).Select(AdminLive).ToArray(),
    };

    public AdminHistoryDto? AdminHistory(int nodeId)
    {
        var n = registry.Get(nodeId);
        if (n is null) return null;
        var pts = n.History.Snapshot();
        return new AdminHistoryDto
        {
            Id = nodeId,
            Ts = pts.Select(p => p.Ts).ToArray(),
            Cpu = pts.Select(p => p.Cpu).ToArray(),
            Mem = pts.Select(p => p.MemPermille).ToArray(),
            Rx = pts.Select(p => p.RxBps).ToArray(),
            Tx = pts.Select(p => p.TxBps).ToArray(),
        };
    }
}
