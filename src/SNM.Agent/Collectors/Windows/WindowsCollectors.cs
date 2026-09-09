using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SNM.Agent.Collectors.Shared;
using SNM.Agent.Logging;
using SNM.Contracts;

namespace SNM.Agent.Collectors.Windows;

[SupportedOSPlatform("windows")]
internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetLogicalProcessorInformationEx(int relationshipType, byte* buffer, ref uint returnedLength);
}

[SupportedOSPlatform("windows")]
internal static partial class IpHlpApi
{
    /// <summary>MIB_IF_ROW2 as laid out in netioapi.h (sizeof == 1352).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu;
        public uint Type;
        public int TunnelType;
        public int MediaType;
        public int PhysicalMediumType;
        public int AccessType;
        public int DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public int OperStatus;
        public int AdminStatus;
        public int MediaConnectState;
        public Guid NetworkGuid;
        public int ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }

    internal const int ExpectedRowSize = 1352;

    [LibraryImport("iphlpapi.dll")]
    internal static unsafe partial uint GetIfTable2(void** table);

    [LibraryImport("iphlpapi.dll")]
    internal static unsafe partial void FreeMibTable(void* memory);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsCpuSampler(ErrorThrottle throttle) : ICpuSampler
{
    private long _lastIdle, _lastBusy;
    private bool _hasBaseline;
    private ushort _lastValue;

    public ushort Sample()
    {
        if (!Kernel32.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throttle.Warn("cpu", $"GetSystemTimes failed ({Marshal.GetLastPInvokeError()})");
            return _lastValue;
        }
        var busy = kernel + user;   // kernel time includes idle
        if (_hasBaseline)
        {
            var dBusy = busy - _lastBusy;
            var dIdle = idle - _lastIdle;
            if (dBusy > 0 && dIdle >= 0 && dIdle <= dBusy) _lastValue = (ushort)Math.Clamp(Math.Round(1000.0 * (dBusy - dIdle) / dBusy), 0, ProtocolConstants.CpuPermilleMax);
        }
        _lastBusy = busy;
        _lastIdle = idle;
        _hasBaseline = true;
        return _lastValue;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsMemorySampler(ErrorThrottle throttle) : IMemorySampler
{
    private MemorySample _last;

    public MemorySample Sample()
    {
        var m = new Kernel32.MEMORYSTATUSEX { dwLength = (uint)Unsafe.SizeOf<Kernel32.MEMORYSTATUSEX>() };
        if (!Kernel32.GlobalMemoryStatusEx(ref m))
        {
            throttle.Warn("mem", $"GlobalMemoryStatusEx failed ({Marshal.GetLastPInvokeError()})");
            return _last;
        }
        var usedPhys = m.ullTotalPhys > m.ullAvailPhys ? m.ullTotalPhys - m.ullAvailPhys : 0;
        var swapTotal = m.ullTotalPageFile > m.ullTotalPhys ? m.ullTotalPageFile - m.ullTotalPhys : 0;
        var commitUsed = m.ullTotalPageFile > m.ullAvailPageFile ? m.ullTotalPageFile - m.ullAvailPageFile : 0;
        var swapUsed = commitUsed > usedPhys ? commitUsed - usedPhys : 0;
        _last = new MemorySample(Mb(m.ullTotalPhys), Mb(usedPhys), Mb(swapTotal), Mb(Math.Min(swapUsed, swapTotal)));
        return _last;
        static uint Mb(ulong b) => (uint)Math.Min(b / (ulong)ProtocolConstants.MiB, uint.MaxValue);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ZeroLoadSampler : ILoadSampler
{
    public ushort Load1x100() => 0;
}

/// <summary>GetIfTable2 sum over physical, connected adapters (docs/PROTOCOL.md 7.4-7.5).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsNetSampler : INetSampler
{
    private readonly string[]? _explicit;
    private readonly ErrorThrottle _throttle;
    private readonly bool _layoutOk;
    private NetSample _last = new(0, 0, []);

    public WindowsNetSampler(string[]? explicitIfs, ErrorThrottle throttle)
    {
        _explicit = explicitIfs;
        _throttle = throttle;
        _layoutOk = Unsafe.SizeOf<IpHlpApi.MIB_IF_ROW2>() == IpHlpApi.ExpectedRowSize;
        if (!_layoutOk) AgentLog.Error($"MIB_IF_ROW2 layout mismatch ({Unsafe.SizeOf<IpHlpApi.MIB_IF_ROW2>()} != {IpHlpApi.ExpectedRowSize}); network counters disabled");
    }

    public unsafe NetSample Sample()
    {
        if (!_layoutOk) return _last;
        void* table = null;
        var rc = IpHlpApi.GetIfTable2(&table);
        if (rc != 0 || table is null)
        {
            _throttle.Warn("net", $"GetIfTable2 failed ({rc})");
            return _last;
        }
        try
        {
            var count = *(uint*)table;
            var rows = (IpHlpApi.MIB_IF_ROW2*)((byte*)table + 8);
            ulong rx = 0, tx = 0;
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint i = 0; i < count; i++)
            {
                var row = &rows[i];
                var alias = new string(row->Alias);
                var description = new string(row->Description);
                seen.Add(alias);
                // bit 1 = FilterInterface: NDIS lightweight-filter bindings (WFP, QoS scheduler) mirror the adapter's counters
                if ((row->InterfaceAndOperStatusFlags & 0x02) != 0) continue;
                bool counted = _explicit is not null
                    ? _explicit.Any(e => string.Equals(e, alias, StringComparison.OrdinalIgnoreCase))
                    : NicFilter.IsWindowsCounted(row->Type, row->OperStatus, alias, description);
                if (!counted) continue;
                rx += row->InOctets;
                tx += row->OutOctets;
                names.Add(alias);
            }
            if (_explicit is not null)
            {
                foreach (var e in _explicit)
                {
                    if (!seen.Contains(e)) _throttle.Warn("net:missing:" + e, $"--net-if {e} not found");
                }
            }
            _last = new NetSample(rx, tx, names.ToArray());
            return _last;
        }
        finally
        {
            IpHlpApi.FreeMibTable(table);
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsSystemInfo : ISystemInfo
{
    public WindowsSystemInfo(string? nameOverride)
    {
        Hostname = nameOverride ?? Environment.MachineName;
        Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        CpuCores = (ushort)Math.Clamp(Environment.ProcessorCount, 1, ushort.MaxValue);
        var model = ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? "Unknown CPU";
        var sockets = CountSockets();
        CpuModel = sockets >= 2 ? $"{sockets}x {model}" : model;
        var product = ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
        var display = ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") ?? "";
        var build = ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber") ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = ReadRegistryInt(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        Os = $"{product} {display} (build {build}.{ubr})".Replace("  ", " ");
        Kernel = $"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{Environment.OSVersion.Version.Build}.{ubr}";
        Virt = DetectVirt();
    }

    public string Hostname { get; }
    public string Os { get; }
    public string Kernel { get; }
    public string Arch { get; }
    public string CpuModel { get; }
    public ushort CpuCores { get; }
    public string Virt { get; }

    public uint UptimeSec() => (uint)Math.Clamp(Environment.TickCount64 / 1000, 0, uint.MaxValue);

    public ushort ProcCount()
    {
        try
        {
            var procs = Process.GetProcesses();
            var n = procs.Length;
            foreach (var p in procs) p.Dispose();
            return (ushort)Math.Min(n, ushort.MaxValue);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return 0;
        }
    }

    private static string? ReadRegistry(string key, string value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(value) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static int ReadRegistryInt(string key, string value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(value) is int i ? i : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    private static unsafe int CountSockets()
    {
        const int RelationProcessorPackage = 3;
        uint length = 0;
        if (Kernel32.GetLogicalProcessorInformationEx(RelationProcessorPackage, null, ref length) || length == 0) return 1;
        if (Marshal.GetLastPInvokeError() != 122) return 1;   // ERROR_INSUFFICIENT_BUFFER expected
        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (!Kernel32.GetLogicalProcessorInformationEx(RelationProcessorPackage, p, ref length)) return 1;
            var count = 0;
            uint offset = 0;
            while (offset + 8 <= length)
            {
                var relationship = *(int*)(p + offset);
                var size = *(uint*)(p + offset + 4);
                if (size == 0) break;
                if (relationship == RelationProcessorPackage) count++;
                offset += size;
            }
            return Math.Max(count, 1);
        }
    }

    private static string DetectVirt()
    {
        var manufacturer = ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer") ?? "";
        var product = ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName") ?? "";
        var s = manufacturer + " " + product;
        if (s.Contains("QEMU", StringComparison.OrdinalIgnoreCase) || s.Contains("KVM", StringComparison.OrdinalIgnoreCase) || s.Contains("Standard PC", StringComparison.OrdinalIgnoreCase)) return "kvm";
        if (s.Contains("VMware", StringComparison.OrdinalIgnoreCase)) return "vmware";
        if (s.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) && s.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase)) return "hyperv";
        if (s.Contains("Xen", StringComparison.OrdinalIgnoreCase)) return "xen";
        if (s.Contains("innotek", StringComparison.OrdinalIgnoreCase) || s.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)) return "virtualbox";
        if (s.Contains("Parallels", StringComparison.OrdinalIgnoreCase)) return "parallels";
        return "";
    }
}
