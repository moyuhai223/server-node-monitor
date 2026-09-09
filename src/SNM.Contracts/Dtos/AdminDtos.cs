using MessagePack;

namespace SNM.Contracts.Dtos;

// Admin hub DTOs (JWT protected). String keys => JS objects.

[MessagePackObject]
public sealed class AdminNodeLiveDto
{
    [Key("id")] public int Id { get; set; }
    [Key("status")] public byte Status { get; set; }
    [Key("connected")] public bool Connected { get; set; }
    [Key("cpu")] public ushort Cpu { get; set; }
    [Key("memUsedMb")] public uint MemUsedMb { get; set; }
    [Key("swapUsedMb")] public uint SwapUsedMb { get; set; }
    [Key("diskUsedMb")] public ulong[] DiskUsedMb { get; set; } = [];
    [Key("rx")] public ulong RxBps { get; set; }
    [Key("tx")] public ulong TxBps { get; set; }
    [Key("load1")] public ushort Load1 { get; set; }
    [Key("up")] public uint UptimeSec { get; set; }
    [Key("lastSeen")] public long LastSeenTs { get; set; }
    [Key("remoteIp")] public string RemoteIp { get; set; } = "";
    [Key("tUsed")] public ulong TrafficUsedBytes { get; set; }
    [Key("tRx")] public ulong TrafficRxBytes { get; set; }
    [Key("tTx")] public ulong TrafficTxBytes { get; set; }
    [Key("seq")] public uint Seq { get; set; }
}

[MessagePackObject]
public sealed class AdminSnapshotDto
{
    [Key("ts")] public long ServerTs { get; set; }
    [Key("nodes")] public AdminNodeLiveDto[] Nodes { get; set; } = [];
}

[MessagePackObject]
public sealed class AdminBatchDto
{
    [Key("ts")] public long ServerTs { get; set; }
    [Key("items")] public AdminNodeLiveDto[] Items { get; set; } = [];
}

[MessagePackObject]
public sealed class AdminAlertDto
{
    [Key("id")] public long Id { get; set; }
    [Key("nodeId")] public int NodeId { get; set; }
    [Key("nodeName")] public string NodeName { get; set; } = "";
    [Key("rule")] public byte Rule { get; set; }
    [Key("status")] public byte Status { get; set; }
    [Key("severity")] public byte Severity { get; set; }
    [Key("title")] public string Title { get; set; } = "";
    [Key("message")] public string Message { get; set; } = "";
    [Key("ts")] public long Ts { get; set; }
}

[MessagePackObject]
public sealed class AdminHistoryDto
{
    [Key("id")] public int Id { get; set; }
    [Key("ts")] public long[] Ts { get; set; } = [];
    [Key("cpu")] public ushort[] Cpu { get; set; } = [];
    [Key("mem")] public ushort[] Mem { get; set; } = [];
    [Key("rx")] public ulong[] Rx { get; set; } = [];
    [Key("tx")] public ulong[] Tx { get; set; } = [];
}
