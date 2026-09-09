using System.Runtime.InteropServices;
using SNM.Agent.Collectors.Linux;

namespace SNM.Agent.Tests;

/// <summary>Parsers are pure functions over text and run on every OS; the samplers themselves are Linux-only.</summary>
public class ProcParserTests
{
    [Fact]
    public void ProcStatTotalsExcludeGuestAndCountIoWaitAsIdle()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        Assert.True(ProcStatCpuSampler.TryParse("cpu  100 2 30 800 20 3 4 1 50 5", out var total, out var idle));
        Assert.Equal(100ul + 2 + 30 + 800 + 20 + 3 + 4 + 1, total);
        Assert.Equal(820ul, idle);
        Assert.False(ProcStatCpuSampler.TryParse("cpu0 1 2 3 4", out _, out _));
        Assert.True(ProcStatCpuSampler.TryParse("cpu 10 0 10 80", out total, out idle));   // old kernels: 4 fields
        Assert.Equal(100ul, total);
        Assert.Equal(80ul, idle);
    }

    [Fact]
    public void MemInfoUsesMemAvailableWhenPresent()
    {
        var m = ProcMemInfoSampler.Parse(["MemTotal:        2048000 kB", "MemFree:          100000 kB", "MemAvailable:    1024000 kB",
            "Buffers:           50000 kB", "Cached:           300000 kB", "SwapTotal:       1048576 kB", "SwapFree:         524288 kB"]);
        Assert.Equal(2000u, m.TotalMb);
        Assert.Equal(1000u, m.UsedMb);
        Assert.Equal(1024u, m.SwapTotalMb);
        Assert.Equal(512u, m.SwapUsedMb);
    }

    [Fact]
    public void MemInfoFallsBackWithoutMemAvailable()
    {
        var m = ProcMemInfoSampler.Parse(["MemTotal:        1024000 kB", "MemFree:          200000 kB", "Buffers:           24000 kB", "Cached:           300000 kB", "SReclaimable:      12000 kB"]);
        // available = 200000 + 24000 + 300000 + 12000 = 536000 kB -> used = 488000 kB = 476 MiB
        Assert.Equal(1000u, m.TotalMb);
        Assert.Equal(476u, m.UsedMb);
        Assert.Equal(0u, m.SwapTotalMb);
    }

    [Fact]
    public void NetDevLineWithAndWithoutSpaceAfterColon()
    {
        Assert.True(ProcNetDevSampler.TryParseLine("  eth0: 1234567 100 0 0 0 0 0 0 7654321 90 0 0 0 0 0 0", out var name, out var rx, out var tx));
        Assert.Equal("eth0", name);
        Assert.Equal(1234567ul, rx);
        Assert.Equal(7654321ul, tx);
        Assert.True(ProcNetDevSampler.TryParseLine("ens18:98765432101 1 2 3 4 5 6 7 12345 8 9 10 11 12 13 14", out name, out rx, out tx));
        Assert.Equal("ens18", name);
        Assert.Equal(98765432101ul, rx);
        Assert.Equal(12345ul, tx);
        Assert.False(ProcNetDevSampler.TryParseLine("Inter-|   Receive", out _, out _, out _));
    }

    [Fact]
    public void MountsAreFilteredDedupedAndRootFirst()
    {
        var mounts = ProcMountsDiskSampler.ParseMounts([
            "/dev/sda2 /data xfs rw 0 0",
            "/dev/sda1 / ext4 rw 0 0",
            "/dev/sda1 /home ext4 rw 0 0",                   // bind mount of the same device
            "tmpfs /run tmpfs rw 0 0",
            "/dev/sdb1 /mnt/my\\040disk ntfs3 rw 0 0",
            "/dev/loop3 /snap/core/1 squashfs ro 0 0",
            "/dev/sda1 /boot/efi vfat rw 0 0",
        ]);
        var selected = ProcMountsDiskSampler.Select(mounts, null);
        Assert.Equal(["/", "/data", "/mnt/my disk"], selected.Select(m => m.Mount).ToArray());

        var explicitOnly = ProcMountsDiskSampler.Select(mounts, ["/data"]);
        Assert.Single(explicitOnly);
        Assert.Equal("/data", explicitOnly[0].Mount);
    }

    [Fact]
    public void CpuInfoCountsSocketsAndProcessors()
    {
        var lines = new List<string>();
        for (var p = 0; p < 8; p++)
        {
            lines.Add($"processor\t: {p}");
            lines.Add("model name\t: Intel(R) Xeon(R) Gold 6148 CPU @ 2.40GHz");
            lines.Add($"physical id\t: {p / 4}");
            lines.Add("");
        }
        var (model, cores) = LinuxSystemInfo.ParseCpuInfo(lines);
        Assert.Equal("2x Intel(R) Xeon(R) Gold 6148 CPU @ 2.40GHz", model);
        Assert.Equal(8, cores);
    }

    [Fact]
    public void CpuInfoArmFallbacks()
    {
        var (model, cores) = LinuxSystemInfo.ParseCpuInfo(["processor\t: 0", "CPU implementer\t: 0x41", "CPU part\t: 0xd0c", "processor\t: 1", "CPU implementer\t: 0x41", "CPU part\t: 0xd0c"]);
        Assert.Equal("ARM64 CPU (implementer 0x41, part 0xd0c)", model);
        Assert.Equal(2, cores);
        (model, _) = LinuxSystemInfo.ParseCpuInfo(["processor\t: 0", "Hardware\t: BCM2835"]);
        Assert.Equal("BCM2835", model);
    }

    [Fact]
    public void LinuxSamplersProduceValuesOnLinuxOnly()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        var throttle = new SNM.Agent.Logging.ErrorThrottle();
        var cpu = new ProcStatCpuSampler(throttle);
        cpu.Sample();
        Thread.Sleep(200);
        Assert.InRange(cpu.Sample(), 0, 1000);
        var mem = new ProcMemInfoSampler(throttle).Sample();
        Assert.True(mem.TotalMb > 0);
        var net = new ProcNetDevSampler(null, throttle).Sample();
        Assert.NotNull(net.Interfaces);
    }
}
