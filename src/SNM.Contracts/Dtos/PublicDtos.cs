using MessagePack;

namespace SNM.Contracts.Dtos;

// Browser-facing DTOs use string keys (map form) so that JS receives plain objects.
// NEVER add IP addresses, hostnames, remarks, vendor/price/expiry or keys to any type in this file:
// everything here is visible to anonymous visitors of the public dashboard.

[MessagePackObject]
public sealed class PublicSiteDto
{
    [Key("title")] public string Title { get; set; } = "";
    [Key("subtitle")] public string Subtitle { get; set; } = "";
    [Key("showSpecs")] public bool ShowSpecs { get; set; }
    [Key("showTraffic")] public bool ShowTraffic { get; set; }
    [Key("offlineSec")] public int OfflineTimeoutSec { get; set; }
    /// <summary>Id of the active public theme; themes reload when it changes.</summary>
    [Key("theme")] public string Theme { get; set; } = "default";
    /// <summary>Free-form JSON string with theme options edited by the admin (site.themeOptions).</summary>
    [Key("opts")] public string ThemeOptions { get; set; } = "";
}

[MessagePackObject]
public sealed class PublicNodeLiveDto
{
    [Key("id")] public int Id { get; set; }
    [Key("status")] public byte Status { get; set; }
    /// <summary>CPU permille.</summary>
    [Key("cpu")] public ushort Cpu { get; set; }
    /// <summary>Memory used, permille of MemTotal.</summary>
    [Key("mem")] public ushort Mem { get; set; }
    /// <summary>Disk used, permille of the summed capacity.</summary>
    [Key("disk")] public ushort Disk { get; set; }
    [Key("rx")] public ulong RxBps { get; set; }
    [Key("tx")] public ulong TxBps { get; set; }
    [Key("up")] public uint UptimeSec { get; set; }
    [Key("tUsed")] public ulong TrafficUsedBytes { get; set; }
    /// <summary>Unix ms of the last heartbeat, 0 = never.</summary>
    [Key("ts")] public long Ts { get; set; }
}

[MessagePackObject]
public sealed class PublicHistoryDto
{
    [Key("cpu")] public ushort[] Cpu { get; set; } = [];
    [Key("mem")] public ushort[] Mem { get; set; } = [];
    [Key("rx")] public ulong[] Rx { get; set; } = [];
    [Key("tx")] public ulong[] Tx { get; set; } = [];
}

[MessagePackObject]
public sealed class PublicNodeDto
{
    [Key("id")] public int Id { get; set; }
    /// <summary>PublicName only - never the hostname or the admin remark.</summary>
    [Key("name")] public string Name { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2 upper-case country code or "".</summary>
    [Key("cc")] public string Cc { get; set; } = "";
    [Key("order")] public int Order { get; set; }
    [Key("cores")] public ushort Cores { get; set; }
    [Key("memMb")] public uint MemTotalMb { get; set; }
    [Key("diskMb")] public ulong DiskTotalMb { get; set; }
    [Key("tLimit")] public ulong TrafficLimitBytes { get; set; }
    [Key("live")] public PublicNodeLiveDto Live { get; set; } = new();
    [Key("hist")] public PublicHistoryDto? Hist { get; set; }
}

[MessagePackObject]
public sealed class PublicSnapshotDto
{
    [Key("ts")] public long ServerTs { get; set; }
    [Key("site")] public PublicSiteDto Site { get; set; } = new();
    [Key("nodes")] public PublicNodeDto[] Nodes { get; set; } = [];
}

[MessagePackObject]
public sealed class PublicBatchDto
{
    [Key("ts")] public long ServerTs { get; set; }
    [Key("items")] public PublicNodeLiveDto[] Items { get; set; } = [];
}
