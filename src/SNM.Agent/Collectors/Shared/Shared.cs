using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SNM.Agent.Logging;
using SNM.Contracts;
using SNM.Contracts.Dtos;

namespace SNM.Agent.Collectors.Shared;

/// <summary>NIC name rules shared by traffic counting and IP discovery (docs/PROTOCOL.md 7.5-7.6).</summary>
internal static class NicFilter
{
    private static readonly string[] ExcludedPrefixes =
    [
        "lo", "docker", "br-", "veth", "virbr", "vnet", "lxcbr", "lxdbr", "cni", "flannel", "kube", "cali",
        "tun", "tap", "wg", "tailscale", "zt", "dummy", "ifb", "gre", "gretap", "erspan", "ip6tnl", "ip_vti", "sit", "nlmon", "bonding_masters",
    ];

    private static readonly string[] TunnelPrefixes = ["tun", "tap", "wg", "tailscale", "zt"];

    /// <summary>True when the interface must not contribute to traffic counters.</summary>
    public static bool IsExcludedForTraffic(string name)
    {
        if (name == "lo") return true;
        foreach (var p in ExcludedPrefixes)
        {
            if (p == "lo") continue;
            if (name.StartsWith(p, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>IP discovery keeps tunnel interfaces so admins can see overlay addresses.</summary>
    public static bool IsExcludedForIps(string name)
    {
        if (!IsExcludedForTraffic(name)) return false;
        foreach (var p in TunnelPrefixes)
        {
            if (name.StartsWith(p, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static readonly string[] WindowsAliasPrefixes = ["vEthernet", "Loopback", "Local Area Connection*"];

    private static readonly string[] WindowsDescriptionBlacklist =
    [
        "Hyper-V Virtual Ethernet Adapter", "VirtualBox Host-Only", "VMware Virtual Ethernet Adapter", "TAP-Windows", "Wintun", "WireGuard", "Tailscale",
        "ZeroTier", "Npcap Loopback", "Bluetooth Device", "Wi-Fi Direct Virtual", "Teredo", "ISATAP", "6to4", "Kernel Debug Network Adapter",
    ];

    public static bool IsWindowsCounted(uint type, int operStatus, string alias, string description)
    {
        if (type is not (6 or 71) || operStatus != 1) return false;
        foreach (var p in WindowsAliasPrefixes)
        {
            if (alias.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
        }
        foreach (var d in WindowsDescriptionBlacklist)
        {
            if (description.Contains(d, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}

/// <summary>Local address discovery (docs/PROTOCOL.md 7.6). Public/private classification happens on the master.</summary>
internal sealed class IpDiscovery : IIpDiscovery
{
    public string[] Discover()
    {
        var v4 = new SortedSet<string>(StringComparer.Ordinal);
        var v6 = new SortedSet<string>(StringComparer.Ordinal);
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return []; }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (OperatingSystem.IsLinux() && NicFilter.IsExcludedForIps(nic.Name)) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
            foreach (var ua in props.UnicastAddresses)
            {
                var ip = ua.Address;
                if (!IsReportable(ip)) continue;
                if (OperatingSystem.IsWindows())
                {
                    if (ua.SuffixOrigin == SuffixOrigin.Random || ua.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred) continue;
                }
                if (ip.AddressFamily == AddressFamily.InterNetwork) v4.Add(ip.ToString());
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    var noScope = ip.ScopeId != 0 ? new IPAddress(ip.GetAddressBytes()) : ip;
                    v6.Add(noScope.ToString());
                }
            }
        }

        var all = new List<string>(v4.Count + v6.Count);
        all.AddRange(v4);
        all.AddRange(v6);
        if (all.Count <= ProtocolConstants.MaxIps) return all.ToArray();
        // over the cap: keep public addresses first, then the rest in order
        var ordered = all.OrderBy(a => LooksPrivate(a) ? 1 : 0).ThenBy(a => all.IndexOf(a)).Take(ProtocolConstants.MaxIps).ToList();
        return ordered.OrderBy(a => a.Contains(':') ? 1 : 0).ThenBy(a => a, StringComparer.Ordinal).ToArray();
    }

    public static bool IsReportable(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 169 && b[1] == 254) && b[0] != 0 && b[0] < 224;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo);
        return false;
    }

    private static bool LooksPrivate(string s)
    {
        if (!IPAddress.TryParse(s, out var ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && (b[1] & 0xC0) == 64);
        }
        var f = ip.GetAddressBytes()[0];
        return (f & 0xFE) == 0xFC || ip.IsIPv6UniqueLocal;
    }
}

/// <summary>Mount filtering for Linux (docs/PROTOCOL.md 7.7).</summary>
internal static class DiskFilter
{
    private static readonly HashSet<string> FsWhitelist = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "jfs", "reiserfs", "bcachefs", "ntfs", "ntfs3", "vfat", "exfat", "fuseblk", "ufs", "hfsplus",
    };

    private static readonly string[] ExcludedMountPrefixes = ["/snap/", "/var/lib/docker/", "/var/lib/containers/", "/run/", "/proc", "/sys", "/dev"];

    public static bool IsCounted(string mount, string fsType)
    {
        if (!FsWhitelist.Contains(fsType)) return false;
        if (mount == "/boot/efi") return false;
        foreach (var p in ExcludedMountPrefixes)
        {
            if (mount.StartsWith(p, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Decodes /proc/mounts octal escapes (\040 = space).</summary>
    public static string DecodeMountPath(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && IsOctal(s[i + 1]) && IsOctal(s[i + 2]) && IsOctal(s[i + 3]))
            {
                sb.Append((char)(((s[i + 1] - '0') << 6) | ((s[i + 2] - '0') << 3) | (s[i + 3] - '0')));
                i += 3;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
        static bool IsOctal(char c) => c is >= '0' and <= '7';
    }
}

/// <summary>Disk sampler based on DriveInfo (Windows; Linux uses ProcMountsDiskSampler which also relies on DriveInfo for sizes).</summary>
internal sealed class DriveInfoDiskSampler(string[]? include, ErrorThrottle throttle) : IDiskSampler
{
    public DiskInfoDto[] Inventory()
    {
        var list = new List<DiskInfoDto>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    if (include is not null && !include.Any(i => string.Equals(i.TrimEnd('\\', '/'), d.Name.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new DiskInfoDto { Mount = d.Name, Fs = d.DriveFormat, TotalMb = (uint)Math.Min(d.TotalSize / ProtocolConstants.MiB, uint.MaxValue) });
                    if (list.Count >= ProtocolConstants.MaxDisks) break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throttle.Warn("disk:" + d.Name, $"disk {d.Name} unavailable: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throttle.Warn("disk:list", "cannot enumerate drives", ex);
        }
        if (include is not null)
        {
            foreach (var i in include)
            {
                if (!list.Any(x => string.Equals(x.Mount.TrimEnd('\\', '/'), i.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    throttle.Warn("disk:missing:" + i, $"--disk-include {i} not found");
            }
        }
        return list.ToArray();
    }

    public uint[] UsedMb(DiskInfoDto[] inventory) => UsedMbByDriveInfo(inventory, throttle);

    public static uint[] UsedMbByDriveInfo(DiskInfoDto[] inventory, ErrorThrottle throttle)
    {
        var result = new uint[inventory.Length];
        for (var i = 0; i < inventory.Length; i++)
        {
            try
            {
                var d = new DriveInfo(inventory[i].Mount);
                var used = Math.Max(0, d.TotalSize - d.TotalFreeSpace);
                result[i] = (uint)Math.Min(used / ProtocolConstants.MiB, uint.MaxValue);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throttle.Warn("disk:used:" + inventory[i].Mount, $"cannot read usage of {inventory[i].Mount}: {ex.Message}");
            }
        }
        return result;
    }
}
