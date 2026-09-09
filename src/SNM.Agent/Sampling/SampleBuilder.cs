using System.Runtime.InteropServices;
using SNM.Agent.Cli;
using SNM.Agent.Collectors;
using SNM.Contracts;
using SNM.Contracts.Dtos;

namespace SNM.Agent.Sampling;

/// <summary>Turns collector output into wire DTOs and keeps the disk inventory / IP set consistent between heartbeats and status reports.</summary>
internal sealed class SampleBuilder(ICollectorSet c)
{
    private readonly Lock _sync = new();
    private DiskInfoDto[] _inventory = [];
    private string[] _ips = [];
    private string _netIfs = "";

    /// <summary>Called after every (re)connect: primes the CPU/counter baselines and refreshes the inventory.</summary>
    public void ResetBaseline()
    {
        lock (_sync)
        {
            c.Cpu.Sample();
            _inventory = c.Disk.Inventory();
            _ips = c.Ips.Discover();
            _netIfs = string.Join(',', c.Net.Sample().Interfaces);
        }
    }

    public RegisterDto BuildRegister(ushort intervalMs)
    {
        lock (_sync)
        {
            var mem = c.Memory.Sample();
            var sys = c.System;
            return new RegisterDto
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                AgentVersion = Program.Version.Split('+')[0],
                Hostname = Clip(sys.Hostname, 64),
                Os = Clip(sys.Os, 128),
                Kernel = Clip(sys.Kernel, 64),
                Arch = Clip(sys.Arch, 16),
                CpuModel = Clip(sys.CpuModel, 128),
                CpuCores = sys.CpuCores,
                MemTotalMb = mem.TotalMb,
                SwapTotalMb = mem.SwapTotalMb,
                Disks = _inventory,
                Ips = _ips,
                UptimeSec = sys.UptimeSec(),
                Virt = sys.Virt,
                IntervalMs = intervalMs,
                NetIfs = Clip(_netIfs, ProtocolConstants.MaxStringChars),
            };
        }
    }

    public HeartbeatDto BuildHeartbeat(uint seq, uint elapsedMs)
    {
        lock (_sync)
        {
            var mem = c.Memory.Sample();
            var net = c.Net.Sample();
            return new HeartbeatDto
            {
                Seq = seq,
                ElapsedMs = elapsedMs,
                Cpu = c.Cpu.Sample(),
                MemUsedMb = mem.UsedMb,
                SwapUsedMb = mem.SwapUsedMb,
                DiskUsedMb = _inventory.Length == 0 ? null : c.Disk.UsedMb(_inventory),
                NetRxBytes = net.RxBytes,
                NetTxBytes = net.TxBytes,
                Load1 = c.Load.Load1x100(),
            };
        }
    }

    /// <summary>Returns a status report when the IP/mount set changed or the periodic report is due; null otherwise.</summary>
    public StatusReportDto? BuildStatusIfNeeded(bool due)
    {
        var ips = c.Ips.Discover();
        var inventory = c.Disk.Inventory();
        var net = c.Net.Sample();
        var netIfs = string.Join(',', net.Interfaces);
        lock (_sync)
        {
            var ipsChanged = !ips.SequenceEqual(_ips, StringComparer.Ordinal);
            var disksChanged = !DisksEqual(inventory, _inventory);
            var netChanged = netIfs != _netIfs;
            if (!due && !ipsChanged && !disksChanged && !netChanged) return null;
            _ips = ips;
            _inventory = inventory;
            _netIfs = netIfs;
            return new StatusReportDto
            {
                Ips = ips,
                Disks = inventory,
                UptimeSec = c.System.UptimeSec(),
                ProcCount = c.System.ProcCount(),
                NetIfs = Clip(netIfs, ProtocolConstants.MaxStringChars),
                MemTotalMb = c.Memory.Sample().TotalMb,
            };
        }
    }

    public StatusReportDto BuildStatusNow()
    {
        return BuildStatusIfNeeded(due: true)!;
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

    private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];
}

/// <summary>`snm-agent test`: one full sample set printed as text, no network (docs/PROTOCOL.md 7.1).</summary>
internal static class TestCommand
{
    public static async Task RunAsync(CliOptions opts, SampleBuilder sampler)
    {
        Console.WriteLine($"snm-agent {Program.Version} test on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sampler.ResetBaseline();
        await Task.Delay(1000);
        var reg = sampler.BuildRegister(opts.IntervalMs);
        var hb = sampler.BuildHeartbeat(1, 1000);
        var st = sampler.BuildStatusNow();

        Console.WriteLine();
        Console.WriteLine("RegisterDto");
        Console.WriteLine($"  protocolVersion = {reg.ProtocolVersion}");
        Console.WriteLine($"  agentVersion    = {reg.AgentVersion}");
        Console.WriteLine($"  hostname        = {reg.Hostname}");
        Console.WriteLine($"  os              = {reg.Os}");
        Console.WriteLine($"  kernel          = {reg.Kernel}");
        Console.WriteLine($"  arch            = {reg.Arch}");
        Console.WriteLine($"  cpuModel        = {reg.CpuModel}");
        Console.WriteLine($"  cpuCores        = {reg.CpuCores}");
        Console.WriteLine($"  memTotalMb      = {reg.MemTotalMb}");
        Console.WriteLine($"  swapTotalMb     = {reg.SwapTotalMb}");
        Console.WriteLine($"  uptimeSec       = {reg.UptimeSec}");
        Console.WriteLine($"  virt            = {(reg.Virt.Length == 0 ? "(bare metal / unknown)" : reg.Virt)}");
        Console.WriteLine($"  netIfs          = {reg.NetIfs}");
        Console.WriteLine($"  ips             = {(reg.Ips is null ? "" : string.Join(", ", reg.Ips))}");
        Console.WriteLine("  disks:");
        if (reg.Disks is not null)
        {
            for (var i = 0; i < reg.Disks.Length; i++)
                Console.WriteLine($"    [{i}] {reg.Disks[i].Mount} ({reg.Disks[i].Fs}) total={reg.Disks[i].TotalMb} MiB used={(hb.DiskUsedMb is not null && i < hb.DiskUsedMb.Length ? hb.DiskUsedMb[i] : 0)} MiB");
        }
        Console.WriteLine();
        Console.WriteLine("HeartbeatDto (after 1 s)");
        Console.WriteLine($"  cpu             = {hb.Cpu / 10.0:F1} %");
        Console.WriteLine($"  memUsedMb       = {hb.MemUsedMb}");
        Console.WriteLine($"  swapUsedMb      = {hb.SwapUsedMb}");
        Console.WriteLine($"  netRxBytes      = {hb.NetRxBytes}");
        Console.WriteLine($"  netTxBytes      = {hb.NetTxBytes}");
        Console.WriteLine($"  load1           = {hb.Load1 / 100.0:F2}");
        Console.WriteLine();
        Console.WriteLine("StatusReportDto");
        Console.WriteLine($"  procCount       = {st.ProcCount}");
        Console.WriteLine($"  memTotalMb      = {st.MemTotalMb}");
        Console.WriteLine();
        Console.WriteLine(opts.Server.Length > 0 ? $"server: {opts.Server} (not contacted in test mode)" : "server: (none)");
    }
}
