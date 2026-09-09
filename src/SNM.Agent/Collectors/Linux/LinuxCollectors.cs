using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SNM.Agent.Collectors.Shared;
using SNM.Agent.Logging;
using SNM.Contracts;
using SNM.Contracts.Dtos;

namespace SNM.Agent.Collectors.Linux;

/// <summary>/proc/stat first line -> CPU permille between calls (docs/PROTOCOL.md 7.3).</summary>
[SupportedOSPlatform("linux")]
internal sealed class ProcStatCpuSampler(ErrorThrottle throttle) : ICpuSampler
{
    private ulong _lastTotal, _lastIdle;
    private ushort _lastValue;
    private bool _hasBaseline;

    public ushort Sample()
    {
        string? line = null;
        try
        {
            foreach (var l in File.ReadLines("/proc/stat")) { line = l; break; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throttle.Warn("cpu", "cannot read /proc/stat", ex);
            return _lastValue;
        }
        if (line is null || !TryParse(line, out var total, out var idle)) return _lastValue;
        if (_hasBaseline)
        {
            var dTotal = total - _lastTotal;
            var dIdle = idle - _lastIdle;
            if (dTotal > 0 && dIdle <= dTotal) _lastValue = (ushort)Math.Clamp(Math.Round(1000.0 * (dTotal - dIdle) / dTotal), 0, ProtocolConstants.CpuPermilleMax);
        }
        _lastTotal = total;
        _lastIdle = idle;
        _hasBaseline = true;
        return _lastValue;
    }

    /// <summary>cpu  user nice system idle iowait irq softirq steal [guest guest_nice]; total excludes guest.</summary>
    public static bool TryParse(string line, out ulong total, out ulong idle)
    {
        total = idle = 0;
        if (!line.StartsWith("cpu ", StringComparison.Ordinal)) return false;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return false;
        var count = Math.Min(parts.Length - 1, 8);
        for (var i = 1; i <= count; i++)
        {
            if (!ulong.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return false;
            total += v;
            if (i is 4 or 5) idle += v;   // idle + iowait
        }
        return true;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class ProcMemInfoSampler(ErrorThrottle throttle) : IMemorySampler
{
    private MemorySample _last;

    public MemorySample Sample()
    {
        try
        {
            _last = Parse(File.ReadLines("/proc/meminfo"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throttle.Warn("mem", "cannot read /proc/meminfo", ex);
        }
        return _last;
    }

    public static MemorySample Parse(IEnumerable<string> lines)
    {
        ulong total = 0, available = 0, free = 0, buffers = 0, cached = 0, sreclaimable = 0, swapTotal = 0, swapFree = 0;
        var hasAvailable = false;
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line.AsSpan(0, colon);
            var rest = line.AsSpan(colon + 1).Trim();
            var space = rest.IndexOf(' ');
            if (space > 0) rest = rest[..space];
            if (!ulong.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var kb)) continue;
            switch (key)
            {
                case "MemTotal": total = kb; break;
                case "MemAvailable": available = kb; hasAvailable = true; break;
                case "MemFree": free = kb; break;
                case "Buffers": buffers = kb; break;
                case "Cached": cached = kb; break;
                case "SReclaimable": sreclaimable = kb; break;
                case "SwapTotal": swapTotal = kb; break;
                case "SwapFree": swapFree = kb; break;
            }
        }
        if (!hasAvailable) available = free + buffers + cached + sreclaimable;
        var used = total > available ? total - available : 0;
        var swapUsed = swapTotal > swapFree ? swapTotal - swapFree : 0;
        return new MemorySample(Mb(total), Mb(used), Mb(swapTotal), Mb(swapUsed));
        static uint Mb(ulong kb) => (uint)Math.Min(kb / 1024, uint.MaxValue);
    }
}

[SupportedOSPlatform("linux")]
internal sealed class ProcLoadAvgSampler(ErrorThrottle throttle) : ILoadSampler
{
    public ushort Load1x100()
    {
        try
        {
            var text = File.ReadAllText("/proc/loadavg");
            var space = text.IndexOf(' ');
            var first = space > 0 ? text.AsSpan(0, space) : text.AsSpan();
            if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var load))
                return (ushort)Math.Clamp(Math.Round(load * 100), 0, ushort.MaxValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throttle.Warn("load", "cannot read /proc/loadavg", ex);
        }
        return 0;
    }
}

/// <summary>/proc/net/dev sum over counted NICs (docs/PROTOCOL.md 7.5).</summary>
[SupportedOSPlatform("linux")]
internal sealed class ProcNetDevSampler(string[]? explicitIfs, ErrorThrottle throttle) : INetSampler
{
    private NetSample _last = new(0, 0, []);

    public NetSample Sample()
    {
        try
        {
            ulong rx = 0, tx = 0;
            var names = new List<string>();
            var found = new HashSet<string>(StringComparer.Ordinal);
            var lineNo = 0;
            foreach (var line in File.ReadLines("/proc/net/dev"))
            {
                if (++lineNo <= 2) continue;
                if (!TryParseLine(line, out var name, out var r, out var t)) continue;
                found.Add(name);
                if (!IsCounted(name)) continue;
                rx += r; tx += t;
                names.Add(name);
            }
            if (explicitIfs is not null)
            {
                foreach (var i in explicitIfs)
                {
                    if (!found.Contains(i)) throttle.Warn("net:missing:" + i, $"--net-if {i} not found in /proc/net/dev");
                }
            }
            _last = new NetSample(rx, tx, names.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throttle.Warn("net", "cannot read /proc/net/dev", ex);
        }
        return _last;
    }

    private bool IsCounted(string name)
    {
        if (explicitIfs is not null) return Array.IndexOf(explicitIfs, name) >= 0;
        if (NicFilter.IsExcludedForTraffic(name)) return false;
        var sys = "/sys/class/net/" + name;
        try
        {
            if (Directory.Exists(sys + "/master") || File.Exists(sys + "/master")) return false;   // bond/team member
            var state = File.Exists(sys + "/operstate") ? File.ReadAllText(sys + "/operstate").Trim() : "unknown";
            if (state is not ("up" or "unknown")) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // sysfs unreadable: keep counting by name rules
        }
        return true;
    }

    /// <summary>"  eth0: 1234 5 0 0 0 0 0 0 5678 ..." -> name, rx_bytes (field 1), tx_bytes (field 9).</summary>
    public static bool TryParseLine(string line, out string name, out ulong rx, out ulong tx)
    {
        name = ""; rx = tx = 0;
        var colon = line.IndexOf(':');
        if (colon <= 0) return false;
        name = line[..colon].Trim();
        var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 9) return false;
        return ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out rx)
            && ulong.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out tx);
    }
}

/// <summary>/proc/mounts + DiskFilter + DriveInfo sizes (docs/PROTOCOL.md 7.7).</summary>
[SupportedOSPlatform("linux")]
internal sealed class ProcMountsDiskSampler(string[]? include, ErrorThrottle throttle) : IDiskSampler
{
    public DiskInfoDto[] Inventory()
    {
        List<(string Dev, string Mount, string Fs)> mounts;
        try { mounts = ParseMounts(File.ReadLines("/proc/mounts")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throttle.Warn("mounts", "cannot read /proc/mounts", ex);
            return [];
        }
        var selected = Select(mounts, include);
        if (include is not null)
        {
            foreach (var i in include)
            {
                if (!selected.Any(m => m.Mount == i)) throttle.Warn("disk:missing:" + i, $"--disk-include {i} is not a mounted file system");
            }
        }
        var result = new List<DiskInfoDto>(selected.Count);
        foreach (var m in selected)
        {
            uint totalMb = 0;
            try { totalMb = (uint)Math.Min(new DriveInfo(m.Mount).TotalSize / ProtocolConstants.MiB, uint.MaxValue); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throttle.Warn("disk:size:" + m.Mount, $"cannot stat {m.Mount}: {ex.Message}");
            }
            result.Add(new DiskInfoDto { Mount = m.Mount, Fs = m.Fs, TotalMb = totalMb });
        }
        return result.ToArray();
    }

    public uint[] UsedMb(DiskInfoDto[] inventory) => DriveInfoDiskSampler.UsedMbByDriveInfo(inventory, throttle);

    public static List<(string Dev, string Mount, string Fs)> ParseMounts(IEnumerable<string> lines)
    {
        var list = new List<(string, string, string)>();
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            list.Add((parts[0], DiskFilter.DecodeMountPath(parts[1]), parts[2]));
        }
        return list;
    }

    /// <summary>Applies whitelist/exclusions, de-duplicates by device, puts "/" first, caps at MaxDisks.</summary>
    public static List<(string Dev, string Mount, string Fs)> Select(List<(string Dev, string Mount, string Fs)> mounts, string[]? include)
    {
        var result = new List<(string, string, string)>();
        var seenDev = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in mounts)
        {
            if (include is not null)
            {
                if (Array.IndexOf(include, m.Mount) < 0) continue;
            }
            else if (!DiskFilter.IsCounted(m.Mount, m.Fs)) continue;
            if (!seenDev.Add(m.Dev)) continue;
            result.Add(m);
        }
        result.Sort((a, b) => a.Item2 == "/" ? -1 : b.Item2 == "/" ? 1 : string.CompareOrdinal(a.Item2, b.Item2));
        if (result.Count > ProtocolConstants.MaxDisks) result.RemoveRange(ProtocolConstants.MaxDisks, result.Count - ProtocolConstants.MaxDisks);
        return result;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxSystemInfo : ISystemInfo
{
    public LinuxSystemInfo(string? nameOverride)
    {
        Hostname = nameOverride ?? ReadFirstLine("/etc/hostname") ?? Environment.MachineName;
        Os = ReadOsRelease() ?? RuntimeInformation.OSDescription;
        Kernel = ReadFirstLine("/proc/sys/kernel/osrelease") ?? Environment.OSVersion.Version.ToString();
        Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var (model, cores) = ParseCpuInfo(SafeReadLines("/proc/cpuinfo"));
        CpuModel = model ?? FallbackArmModel() ?? "Unknown CPU";
        CpuCores = cores > 0 ? cores : (ushort)Math.Clamp(Environment.ProcessorCount, 1, ushort.MaxValue);
        Virt = DetectVirt();
    }

    public string Hostname { get; }
    public string Os { get; }
    public string Kernel { get; }
    public string Arch { get; }
    public string CpuModel { get; }
    public ushort CpuCores { get; }
    public string Virt { get; }

    public uint UptimeSec()
    {
        var text = ReadFirstLine("/proc/uptime");
        if (text is null) return 0;
        var space = text.IndexOf(' ');
        var first = space > 0 ? text.AsSpan(0, space) : text.AsSpan();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var up) ? (uint)Math.Clamp(up, 0, uint.MaxValue) : 0;
    }

    public ushort ProcCount()
    {
        try
        {
            var n = 0;
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (name.Length > 0 && char.IsAsciiDigit(name[0]) && name.All(char.IsAsciiDigit)) n++;
            }
            return (ushort)Math.Min(n, ushort.MaxValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Returns (model with "Nx " prefix for N >= 2 sockets, logical cores).</summary>
    public static (string? Model, ushort Cores) ParseCpuInfo(IEnumerable<string> lines)
    {
        string? model = null;
        string? hardware = null;
        string? implementer = null, part = null;
        var processors = 0;
        var sockets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "processor": processors++; break;
                case "model name": model ??= value; break;
                case "physical id": sockets.Add(value); break;
                case "Hardware": hardware ??= value; break;
                case "CPU implementer": implementer ??= value; break;
                case "CPU part": part ??= value; break;
            }
        }
        model ??= hardware;
        if (model is null && implementer is not null && part is not null) model = $"ARM64 CPU (implementer {implementer}, part {part})";
        if (model is not null && sockets.Count >= 2) model = $"{sockets.Count}x {model}";
        return (model, (ushort)Math.Min(processors, ushort.MaxValue));
    }

    private static string? FallbackArmModel()
    {
        try
        {
            if (File.Exists("/proc/device-tree/model")) return File.ReadAllText("/proc/device-tree/model").TrimEnd('\0', '\n', ' ');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static string? ReadOsRelease()
    {
        foreach (var line in SafeReadLines("/etc/os-release"))
        {
            if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) continue;
            return line[12..].Trim().Trim('"');
        }
        return null;
    }

    private static string DetectVirt()
    {
        var vendor = (ReadFirstLine("/sys/class/dmi/id/sys_vendor") ?? "") + " " + (ReadFirstLine("/sys/class/dmi/id/product_name") ?? "");
        if (File.Exists("/.dockerenv")) return "docker";
        try
        {
            var cgroup = File.Exists("/proc/1/cgroup") ? File.ReadAllText("/proc/1/cgroup") : "";
            if (cgroup.Contains("docker", StringComparison.Ordinal)) return "docker";
            if (cgroup.Contains("lxc", StringComparison.Ordinal)) return "lxc";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (Directory.Exists("/proc/vz") && !Directory.Exists("/proc/bc")) return "openvz";
        if (vendor.Contains("QEMU", StringComparison.OrdinalIgnoreCase) || vendor.Contains("KVM", StringComparison.OrdinalIgnoreCase) || vendor.Contains("Standard PC", StringComparison.OrdinalIgnoreCase)) return "kvm";
        if (vendor.Contains("VMware", StringComparison.OrdinalIgnoreCase)) return "vmware";
        if (vendor.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) && vendor.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase)) return "hyperv";
        if (vendor.Contains("Xen", StringComparison.OrdinalIgnoreCase)) return "xen";
        if (vendor.Contains("innotek", StringComparison.OrdinalIgnoreCase) || vendor.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)) return "virtualbox";
        return "";
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path)) return line.Trim();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] SafeReadLines(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
