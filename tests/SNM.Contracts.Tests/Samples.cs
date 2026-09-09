using SNM.Contracts.Dtos;

namespace SNM.Contracts.Tests;

internal static class Samples
{
    public static HeartbeatDto TypicalHeartbeat() => new()
    {
        Seq = 1234, ElapsedMs = 2001, Cpu = 237, MemUsedMb = 1843, SwapUsedMb = 0,
        DiskUsedMb = [18_432], NetRxBytes = 523_000_000_000, NetTxBytes = 98_000_000_000, Load1 = 45,
    };

    public static HeartbeatDto MaxHeartbeat() => new()
    {
        Seq = uint.MaxValue, ElapsedMs = uint.MaxValue, Cpu = 1000, MemUsedMb = uint.MaxValue, SwapUsedMb = uint.MaxValue,
        DiskUsedMb = [uint.MaxValue, 0, 1, 255, 256, 65535, 65536], NetRxBytes = ulong.MaxValue, NetTxBytes = ulong.MaxValue, Load1 = ushort.MaxValue,
    };

    public static HeartbeatDto MinHeartbeat() => new();

    public static HeartbeatDto EmptyDisksHeartbeat() => new() { Seq = 1, DiskUsedMb = [] };

    public static RegisterDto TypicalRegister() => new()
    {
        AgentVersion = "1.0.0+3f2a9c1", Hostname = "db-hk-01", Os = "Ubuntu 22.04.4 LTS", Kernel = "5.15.0-113-generic", Arch = "x64",
        CpuModel = "2x Intel(R) Xeon(R) Gold 6148 CPU @ 2.40GHz", CpuCores = 80, MemTotalMb = 386_000, SwapTotalMb = 8192,
        Disks = [new DiskInfoDto { Mount = "/", Fs = "ext4", TotalMb = 80_000 }, new DiskInfoDto { Mount = "/data", Fs = "xfs", TotalMb = 2_000_000 }],
        Ips = ["203.0.113.10", "10.0.0.5", "2001:db8::10"], UptimeSec = 864_100, Virt = "kvm", IntervalMs = 2000, NetIfs = "eth0,eth1",
    };

    public static RegisterDto MinimalRegister() => new() { Disks = null, Ips = null };

    public static RegisterDto UnicodeRegister() => new()
    {
        Hostname = "主机-🇭🇰", Os = "Windows Server 2022 Datacenter 21H2 (build 20348.2461)", CpuModel = "Apple M2 Ultra", Disks = [], Ips = [],
    };

    public static StatusReportDto TypicalStatus() => new()
    {
        Ips = ["203.0.113.10", "fe80::1"], Disks = [new DiskInfoDto { Mount = "/", Fs = "ext4", TotalMb = 80_000 }],
        UptimeSec = 100, ProcCount = 231, NetIfs = "eth0", MemTotalMb = 2048,
    };

    public static StatusReportDto NullsStatus() => new();

    public static AgentConfigDto DefaultConfig() => new();

    public static AgentConfigDto MaxConfig() => new() { IntervalMs = 60000, StatusIntervalSec = 3600 };

    public static DiskInfoDto Disk() => new() { Mount = "C:\\", Fs = "NTFS", TotalMb = 500_000 };

    public static bool Equal(HeartbeatDto? a, HeartbeatDto? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Seq == b.Seq && a.ElapsedMs == b.ElapsedMs && a.Cpu == b.Cpu && a.MemUsedMb == b.MemUsedMb && a.SwapUsedMb == b.SwapUsedMb
            && SeqEq(a.DiskUsedMb, b.DiskUsedMb) && a.NetRxBytes == b.NetRxBytes && a.NetTxBytes == b.NetTxBytes && a.Load1 == b.Load1;
    }

    public static bool Equal(RegisterDto? a, RegisterDto? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.ProtocolVersion == b.ProtocolVersion && a.AgentVersion == b.AgentVersion && a.Hostname == b.Hostname && a.Os == b.Os
            && a.Kernel == b.Kernel && a.Arch == b.Arch && a.CpuModel == b.CpuModel && a.CpuCores == b.CpuCores && a.MemTotalMb == b.MemTotalMb
            && a.SwapTotalMb == b.SwapTotalMb && DisksEq(a.Disks, b.Disks) && SeqEq(a.Ips, b.Ips) && a.UptimeSec == b.UptimeSec
            && a.Virt == b.Virt && a.IntervalMs == b.IntervalMs && a.NetIfs == b.NetIfs;
    }

    public static bool Equal(StatusReportDto? a, StatusReportDto? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return SeqEq(a.Ips, b.Ips) && DisksEq(a.Disks, b.Disks) && a.UptimeSec == b.UptimeSec && a.ProcCount == b.ProcCount
            && a.NetIfs == b.NetIfs && a.MemTotalMb == b.MemTotalMb;
    }

    public static bool Equal(AgentConfigDto? a, AgentConfigDto? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.IntervalMs == b.IntervalMs && a.StatusIntervalSec == b.StatusIntervalSec;
    }

    public static bool DisksEq(DiskInfoDto[]? a, DiskInfoDto[]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].Mount != b[i].Mount || a[i].Fs != b[i].Fs || a[i].TotalMb != b[i].TotalMb) return false;
        }
        return true;
    }

    public static bool SeqEq<T>(T[]? a, T[]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.AsSpan().SequenceEqual(b);
    }
}
