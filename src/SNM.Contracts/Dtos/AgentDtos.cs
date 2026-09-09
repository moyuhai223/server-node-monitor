using MessagePack;

namespace SNM.Contracts.Dtos;

// All agent-facing DTOs use integer keys (array form). Keys are contiguous and append-only.
// Every DTO here has a matching static formatter in SNM.Contracts.Formatters that must stay byte-identical
// to MessagePack's DynamicObjectResolver output (enforced by tests/SNM.Contracts.Tests).
// Forbidden here: enum, DateTime/TimeSpan, Nullable<T> value types, Dictionary, decimal, float/double.

/// <summary>Agent -> Master every IntervalMs (wire name "hb"). Typical size 36 B, frame 46 B.</summary>
[MessagePackObject]
public sealed class HeartbeatDto
{
    public const int FieldCount = 9;

    /// <summary>Per-connection sequence, starts at 1. Used for duplicate/out-of-order detection.</summary>
    [Key(0)] public uint Seq { get; set; }
    /// <summary>Monotonic ms since the previous counter sample; 0 on the first sample of a connection.</summary>
    [Key(1)] public uint ElapsedMs { get; set; }
    /// <summary>CPU usage in permille (0..1000).</summary>
    [Key(2)] public ushort Cpu { get; set; }
    /// <summary>Used physical memory, MiB.</summary>
    [Key(3)] public uint MemUsedMb { get; set; }
    /// <summary>Used swap, MiB.</summary>
    [Key(4)] public uint SwapUsedMb { get; set; }
    /// <summary>Used MiB per mount, aligned with the last announced disk inventory (Register/Status). null allowed.</summary>
    [Key(5)] public uint[]? DiskUsedMb { get; set; }
    /// <summary>Cumulative received bytes over the counted NICs (monotonic counter; the server computes deltas).</summary>
    [Key(6)] public ulong NetRxBytes { get; set; }
    /// <summary>Cumulative transmitted bytes over the counted NICs.</summary>
    [Key(7)] public ulong NetTxBytes { get; set; }
    /// <summary>loadavg(1m) x 100; 0 on Windows.</summary>
    [Key(8)] public ushort Load1 { get; set; }
}

[MessagePackObject]
public sealed class DiskInfoDto
{
    public const int FieldCount = 3;

    /// <summary>Mount point (Linux path or Windows drive root).</summary>
    [Key(0)] public string Mount { get; set; } = "";
    /// <summary>File system type (ext4/xfs/NTFS...).</summary>
    [Key(1)] public string Fs { get; set; } = "";
    /// <summary>Total capacity, MiB.</summary>
    [Key(2)] public uint TotalMb { get; set; }
}

/// <summary>Agent -> Master once per connection (wire name "register"); returns AgentConfigDto.</summary>
[MessagePackObject]
public sealed class RegisterDto
{
    public const int FieldCount = 16;

    [Key(0)] public ushort ProtocolVersion { get; set; } = ProtocolConstants.ProtocolVersion;
    [Key(1)] public string AgentVersion { get; set; } = "";
    [Key(2)] public string Hostname { get; set; } = "";
    [Key(3)] public string Os { get; set; } = "";
    [Key(4)] public string Kernel { get; set; } = "";
    [Key(5)] public string Arch { get; set; } = "";
    /// <summary>Prefixed with "Nx " when the machine has N >= 2 CPU sockets.</summary>
    [Key(6)] public string CpuModel { get; set; } = "";
    [Key(7)] public ushort CpuCores { get; set; }
    [Key(8)] public uint MemTotalMb { get; set; }
    [Key(9)] public uint SwapTotalMb { get; set; }
    /// <summary>Disk inventory; its order defines the indexes of HeartbeatDto.DiskUsedMb.</summary>
    [Key(10)] public DiskInfoDto[]? Disks { get; set; }
    /// <summary>Filtered local addresses (IPv4 first). Public/private classification is done by the Master.</summary>
    [Key(11)] public string[]? Ips { get; set; }
    [Key(12)] public uint UptimeSec { get; set; }
    /// <summary>kvm / vmware / hyperv / xen / virtualbox / lxc / docker / openvz / empty.</summary>
    [Key(13)] public string Virt { get; set; } = "";
    /// <summary>Heartbeat interval currently in effect on the agent (CLI/env); the reply may override it.</summary>
    [Key(14)] public ushort IntervalMs { get; set; }
    /// <summary>Comma-separated names of the NICs whose counters are summed (diagnostics).</summary>
    [Key(15)] public string NetIfs { get; set; } = "";
}

/// <summary>Agent -> Master every StatusIntervalSec or when the IP/mount set changes (wire name "status").</summary>
[MessagePackObject]
public sealed class StatusReportDto
{
    public const int FieldCount = 6;

    [Key(0)] public string[]? Ips { get; set; }
    /// <summary>Full disk inventory; redefines the heartbeat disk indexes.</summary>
    [Key(1)] public DiskInfoDto[]? Disks { get; set; }
    [Key(2)] public uint UptimeSec { get; set; }
    [Key(3)] public ushort ProcCount { get; set; }
    [Key(4)] public string NetIfs { get; set; } = "";
    [Key(5)] public uint MemTotalMb { get; set; }
}

/// <summary>Return value of "register" and payload of "configure" - the only downlink data.</summary>
[MessagePackObject]
public sealed class AgentConfigDto
{
    public const int FieldCount = 2;

    /// <summary>Heartbeat interval, 1000..60000 ms (default 2000). Applies from the next cycle.</summary>
    [Key(0)] public ushort IntervalMs { get; set; } = ProtocolConstants.DefaultIntervalMs;
    /// <summary>Status report interval, 60..3600 s (default 300).</summary>
    [Key(1)] public ushort StatusIntervalSec { get; set; } = ProtocolConstants.DefaultStatusIntervalSec;
}
