using System.Text.Json;
using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Master.Data.Entities;

namespace SNM.Master.Runtime;

/// <summary>Latest values reported by a node. Immutable: the hub thread replaces the reference, readers copy it.</summary>
public sealed record LiveSample(
    ushort Cpu, uint MemUsedMb, uint SwapUsedMb, uint[] DiskUsedMb, ulong RxBps, ulong TxBps, ushort Load1, uint UptimeSec, DateTime Ts);

/// <summary>One point of the 60-point live sparkline history.</summary>
public readonly record struct LivePoint(long Ts, ushort Cpu, ushort MemPermille, ulong RxBps, ulong TxBps);

/// <summary>Fixed-capacity ring buffer; single writer, readers take a snapshot copy.</summary>
public sealed class RingBuffer<T>(int capacity) where T : struct
{
    private readonly T[] _items = new T[capacity];
    private int _head;   // next write position
    private int _count;
    private readonly Lock _lock = new();

    public int Capacity => capacity;

    public void Add(in T item)
    {
        lock (_lock)
        {
            _items[_head] = item;
            _head = (_head + 1) % capacity;
            if (_count < capacity) _count++;
        }
    }

    /// <summary>Oldest first.</summary>
    public T[] Snapshot()
    {
        lock (_lock)
        {
            var result = new T[_count];
            var start = (_head - _count + capacity) % capacity;
            for (var i = 0; i < _count; i++) result[i] = _items[(start + i) % capacity];
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock) { _head = 0; _count = 0; }
    }
}

/// <summary>Per-node one-minute aggregation of heartbeats (docs/DATA.md 3.1). Heartbeats never hit the database.</summary>
public sealed class MinuteAccumulator(long bucketTs)
{
    public long BucketTs { get; } = bucketTs;
    public int Samples;
    public long CpuSum; public int CpuMax;
    public long MemSum; public long MemMax;
    public long SwapSum;
    public long DiskUsedSum; public long DiskTotal;
    public double RxBpsSum; public long RxBpsMax; public double TxBpsSum; public long TxBpsMax;
    public long RxBytes; public long TxBytes;
    public long Load1Sum; public int Load1Max;

    public void Add(HeartbeatDto hb, long diskUsedMb, long diskTotalMb, long rxBps, long txBps, long dRx, long dTx)
    {
        Samples++;
        CpuSum += hb.Cpu; CpuMax = Math.Max(CpuMax, hb.Cpu);
        MemSum += hb.MemUsedMb; MemMax = Math.Max(MemMax, hb.MemUsedMb);
        SwapSum += hb.SwapUsedMb;
        DiskUsedSum += diskUsedMb; DiskTotal = diskTotalMb;
        RxBpsSum += rxBps; RxBpsMax = Math.Max(RxBpsMax, rxBps);
        TxBpsSum += txBps; TxBpsMax = Math.Max(TxBpsMax, txBps);
        RxBytes += dRx; TxBytes += dTx;
        Load1Sum += hb.Load1; Load1Max = Math.Max(Load1Max, hb.Load1);
    }

    public Metric1m ToRow(int nodeId)
    {
        var n = Math.Max(Samples, 1);
        return new Metric1m
        {
            NodeId = nodeId, Ts = BucketTs, Samples = Samples,
            CpuAvg = (int)Math.Round(CpuSum / (double)n), CpuMax = CpuMax,
            MemUsedAvgMb = (long)Math.Round(MemSum / (double)n), MemUsedMaxMb = MemMax,
            SwapUsedAvgMb = (long)Math.Round(SwapSum / (double)n),
            DiskUsedMb = (long)Math.Round(DiskUsedSum / (double)n), DiskTotalMb = DiskTotal,
            RxBpsAvg = (long)Math.Round(RxBpsSum / n), RxBpsMax = RxBpsMax,
            TxBpsAvg = (long)Math.Round(TxBpsSum / n), TxBpsMax = TxBpsMax,
            RxBytes = RxBytes, TxBytes = TxBytes,
            Load1Avg = (int)Math.Round(Load1Sum / (double)n), Load1Max = Load1Max,
        };
    }
}

/// <summary>In-memory truth for one node (docs/DESIGN.md 3.1). The database holds a snapshot flushed every minute.</summary>
public sealed class NodeRuntime
{
    public NodeRuntime(Node meta)
    {
        Meta = meta;
        Disks = ParseDisks(meta.DisksJson);
        Status = (byte)meta.Status;
        LastSeenAt = meta.LastSeenAt;
        StatusChangedAt = meta.StatusChangedAt;
        RemoteIp = meta.LastRemoteIp ?? "";
        BootTimeUtc = meta.BootTimeUtc;
    }

    /// <summary>Guards hub-side mutations (register/hb/status may arrive on different connections).</summary>
    public readonly Lock Sync = new();

    public int Id => Meta.Id;

    /// <summary>Latest persisted entity copy; replaced (not mutated) by NodeService on REST changes.</summary>
    public volatile Node Meta;

    public volatile DiskInfoDto[] Disks;

    public string? ConnectionId;
    public bool Connected;
    public DateTime? LastSeenAt;
    public byte Status;
    public DateTime? StatusChangedAt;
    public uint LastSeq;
    public bool InventoryMismatch;
    public string RemoteIp;
    public DateTime? BootTimeUtc;
    public bool Registered;
    public ushort ProcCount;
    /// <summary>Set once per connection when a heartbeat arrives before register (log throttling).</summary>
    public bool WarnedUnregistered;

    public volatile LiveSample? Live;
    public readonly RingBuffer<LivePoint> History = new(ProtocolConstants.HistoryPoints);
    public MinuteAccumulator? Acc;
    public readonly System.Collections.Concurrent.ConcurrentQueue<Metric1m> FlushQueue = new();
    public readonly TrafficRuntime Traffic = new();

    /// <summary>Live values changed since the last broadcast.</summary>
    public volatile bool Dirty;
    /// <summary>Status/LastSeen/RemoteIp changed since the last DB flush.</summary>
    public volatile bool StateDirty;

    public string CountryCode => Meta.CountryCodeOverride ?? Meta.CountryCodeAuto ?? "";

    public static DiskInfoDto[] ParseDisks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize(json, RuntimeJson.Default.DiskInfoDtoArray);
            return list ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeDisks(DiskInfoDto[] disks) => JsonSerializer.Serialize(disks, RuntimeJson.Default.DiskInfoDtoArray);

    public long DiskTotalMb()
    {
        long total = 0;
        foreach (var d in Disks) total += d.TotalMb;
        return total;
    }

    public void MarkSeen(DateTime now)
    {
        LastSeenAt = now;
        if (Status != NodeStatus.Online)
        {
            Status = NodeStatus.Online;
            StatusChangedAt = now;
        }
        StateDirty = true;
        Dirty = true;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(DiskInfoDto[]))]
internal sealed partial class RuntimeJson : System.Text.Json.Serialization.JsonSerializerContext;
