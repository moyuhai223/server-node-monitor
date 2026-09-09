using SNM.Contracts.Dtos;

namespace SNM.Agent.Collectors;

internal readonly record struct MemorySample(uint TotalMb, uint UsedMb, uint SwapTotalMb, uint SwapUsedMb);

internal readonly record struct NetSample(ulong RxBytes, ulong TxBytes, string[] Interfaces);

/// <summary>CPU usage since the previous call, permille 0..1000.</summary>
internal interface ICpuSampler
{
    ushort Sample();
}

internal interface IMemorySampler
{
    MemorySample Sample();
}

internal interface ILoadSampler
{
    /// <summary>loadavg(1m) x 100, 0 when unavailable.</summary>
    ushort Load1x100();
}

internal interface INetSampler
{
    /// <summary>Cumulative rx/tx bytes over the counted NICs (docs/PROTOCOL.md 7.5).</summary>
    NetSample Sample();
}

internal interface IDiskSampler
{
    /// <summary>Filtered mount inventory (docs/PROTOCOL.md 7.7); order defines heartbeat indexes.</summary>
    DiskInfoDto[] Inventory();

    /// <summary>Used MiB aligned with <paramref name="inventory"/>.</summary>
    uint[] UsedMb(DiskInfoDto[] inventory);
}

internal interface ISystemInfo
{
    string Hostname { get; }
    string Os { get; }
    string Kernel { get; }
    string Arch { get; }
    string CpuModel { get; }
    ushort CpuCores { get; }
    string Virt { get; }
    uint UptimeSec();
    ushort ProcCount();
}

internal interface IIpDiscovery
{
    string[] Discover();
}

internal interface ICollectorSet
{
    ICpuSampler Cpu { get; }
    IMemorySampler Memory { get; }
    ILoadSampler Load { get; }
    INetSampler Net { get; }
    IDiskSampler Disk { get; }
    ISystemInfo System { get; }
    IIpDiscovery Ips { get; }
}

internal sealed class CollectorSet(ICpuSampler cpu, IMemorySampler memory, ILoadSampler load, INetSampler net, IDiskSampler disk, ISystemInfo system, IIpDiscovery ips) : ICollectorSet
{
    public ICpuSampler Cpu { get; } = cpu;
    public IMemorySampler Memory { get; } = memory;
    public ILoadSampler Load { get; } = load;
    public INetSampler Net { get; } = net;
    public IDiskSampler Disk { get; } = disk;
    public ISystemInfo System { get; } = system;
    public IIpDiscovery Ips { get; } = ips;
}

internal static class CollectorFactory
{
    public static ICollectorSet CreateLinux(Cli.CliOptions opts)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var throttle = new Logging.ErrorThrottle();
        return new CollectorSet(
            new Linux.ProcStatCpuSampler(throttle),
            new Linux.ProcMemInfoSampler(throttle),
            new Linux.ProcLoadAvgSampler(throttle),
            new Linux.ProcNetDevSampler(opts.NetIf, throttle),
            new Linux.ProcMountsDiskSampler(opts.DiskInclude, throttle),
            new Linux.LinuxSystemInfo(opts.Name),
            new Shared.IpDiscovery());
    }

    public static ICollectorSet CreateWindows(Cli.CliOptions opts)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var throttle = new Logging.ErrorThrottle();
        return new CollectorSet(
            new Windows.WindowsCpuSampler(throttle),
            new Windows.WindowsMemorySampler(throttle),
            new Windows.ZeroLoadSampler(),
            new Windows.WindowsNetSampler(opts.NetIf, throttle),
            new Shared.DriveInfoDiskSampler(opts.DiskInclude, throttle),
            new Windows.WindowsSystemInfo(opts.Name),
            new Shared.IpDiscovery());
    }
}
