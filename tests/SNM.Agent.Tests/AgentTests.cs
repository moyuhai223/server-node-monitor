using System.Net;
using Microsoft.Extensions.Logging;
using SNM.Agent.Cli;
using SNM.Agent.Collectors.Shared;
using SNM.Agent.Net;

namespace SNM.Agent.Tests;

public class CliParseTests
{
    private const string Key = "snmk_CMOCTtU2dbN-toFM4giVnmuTVlwDPKiL_uT2xrcIQ8Y";

    [Fact]
    public void CliOverridesEnvironment()
    {
        var env = new Dictionary<string, string> { ["SNM_SERVER"] = "https://env.example.com", ["SNM_KEY"] = Key, ["SNM_INTERVAL"] = "5000" };
        var ok = CliOptions.TryParse(["run", "--server", "https://cli.example.com/", "--interval=1500"], k => env.GetValueOrDefault(k), out var o, out var err);
        Assert.True(ok, err);
        Assert.Equal("https://cli.example.com", o.Server);
        Assert.Equal(Key, o.Key);
        Assert.Equal(1500, o.IntervalMs);
        Assert.Equal(AgentCommand.Run, o.Command);
    }

    [Fact]
    public void IntervalIsClamped()
    {
        var ok = CliOptions.TryParse(["--server", "http://h", "--key", Key, "--interval", "100"], _ => null, out var o, out var err);
        Assert.True(ok, err);
        Assert.Equal(1000, o.IntervalMs);
        ok = CliOptions.TryParse(["--server", "http://h", "--key", Key, "--interval", "999999"], _ => null, out o, out err);
        Assert.True(ok, err);
        Assert.Equal(60000, o.IntervalMs);
    }

    [Theory]
    [InlineData("snmk_short")]
    [InlineData("abcd_CMOCTtU2dbN-toFM4giVnmuTVlwDPKiL_uT2xrcIQ8Y")]
    [InlineData("snmk_CMOCTtU2dbN-toFM4giVnmuTVlwDPKiL_uT2xrc!Q8Y")]
    public void RejectsBadKeys(string key)
    {
        var ok = CliOptions.TryParse(["--server", "http://h", "--key", key], _ => null, out _, out var err);
        Assert.False(ok);
        Assert.Contains("key", err);
    }

    [Fact]
    public void RejectsServerWithPath()
    {
        var ok = CliOptions.TryParse(["--server", "https://h/hubs/agent", "--key", Key], _ => null, out _, out var err);
        Assert.False(ok);
        Assert.Contains("origin", err);
    }

    [Fact]
    public void RejectsUnknownOptionAndBadProxy()
    {
        Assert.False(CliOptions.TryParse(["--bogus", "1"], _ => null, out _, out var err));
        Assert.Contains("unknown option", err);
        Assert.False(CliOptions.TryParse(["--server", "http://h", "--key", Key, "--proxy", "ftp://x"], _ => null, out _, out err));
        Assert.Contains("proxy", err);
        Assert.True(CliOptions.TryParse(["--server", "http://h", "--key", Key, "--proxy", "none"], _ => null, out var o, out err), err);
        Assert.Equal("none", o.Proxy);
    }

    [Fact]
    public void TestCommandNeedsNoServer()
    {
        Assert.True(CliOptions.TryParse(["test", "--log-level", "debug"], _ => null, out var o, out var err), err);
        Assert.Equal(AgentCommand.Test, o.Command);
        Assert.Equal(LogLevel.Debug, o.LogLevel);
    }

    [Fact]
    public void EnvFileIsLowestPrecedence()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, ["# comment", "SNM_SERVER=https://file.example.com", "SNM_KEY=\"" + Key + "\"", "SNM_NET_IF=eth0, eth1", "export SNM_LOG_LEVEL=warn"]);
        try
        {
            var env = new Dictionary<string, string> { ["SNM_SERVER"] = "https://env.example.com" };
            var ok = CliOptions.TryParse(["--env-file", path], k => env.GetValueOrDefault(k), out var o, out var err);
            Assert.True(ok, err);
            Assert.Equal("https://env.example.com", o.Server);
            Assert.Equal(Key, o.Key);
            Assert.Equal(["eth0", "eth1"], o.NetIf!);
            Assert.Equal(LogLevel.Warning, o.LogLevel);
        }
        finally { File.Delete(path); }
    }
}

public class BackoffTests
{
    [Fact]
    public void SequenceGrowsAndCapsWithJitter()
    {
        var b = new Backoff();
        double[] expected = [1, 2, 4, 8, 16, 32, 60, 60, 60];
        foreach (var e in expected)
        {
            var d = b.Next(FailureKind.Network).TotalSeconds;
            Assert.InRange(d, e * 0.8 - 1e-9, e * 1.2 + 1e-9);
        }
        b.Reset();
        Assert.InRange(b.Next(FailureKind.Network).TotalSeconds, 0.8, 1.2);
    }

    [Fact]
    public void AuthAndProtocolFailuresUseFixedDelays()
    {
        var b = new Backoff();
        Assert.InRange(b.Next(FailureKind.AuthRejected).TotalSeconds, 48, 72);
        Assert.InRange(b.Next(FailureKind.ProtocolMismatch).TotalSeconds, 480, 720);
    }

    [Fact]
    public void LogsFirstTenThenEveryTenth()
    {
        var b = new Backoff();
        for (var i = 1; i <= 10; i++) { b.Next(FailureKind.Network); Assert.True(b.ShouldLog); }
        b.Next(FailureKind.Network);
        Assert.False(b.ShouldLog);
        for (var i = 12; i <= 20; i++) b.Next(FailureKind.Network);
        Assert.True(b.ShouldLog);
    }
}

public class ProxyParserTests
{
    [Fact]
    public void ParsesSocks5WithCredentials()
    {
        var p = ProxyParser.Parse("socks5://user:p%40ss@10.0.0.1:1080", out var direct);
        Assert.False(direct);
        var wp = Assert.IsType<WebProxy>(p);
        Assert.Equal("socks5", wp.Address!.Scheme);
        Assert.Equal(1080, wp.Address.Port);
        var cred = Assert.IsType<NetworkCredential>(wp.Credentials);
        Assert.Equal("user", cred.UserName);
        Assert.Equal("p@ss", cred.Password);
        Assert.DoesNotContain("user", wp.Address.ToString());
    }

    [Fact]
    public void NoneMeansDirect()
    {
        Assert.Null(ProxyParser.Parse("none", out var direct));
        Assert.True(direct);
        Assert.Null(ProxyParser.Parse(null, out direct));
        Assert.False(direct);
    }
}

public class NicFilterTests
{
    [Theory]
    [InlineData("lo", true)]
    [InlineData("docker0", true)]
    [InlineData("br-1a2b", true)]
    [InlineData("veth12ab", true)]
    [InlineData("tailscale0", true)]
    [InlineData("eth0", false)]
    [InlineData("ens18", false)]
    [InlineData("bond0", false)]
    [InlineData("wlan0", false)]
    public void TrafficExclusions(string name, bool excluded) => Assert.Equal(excluded, NicFilter.IsExcludedForTraffic(name));

    [Theory]
    [InlineData("tailscale0", false)]
    [InlineData("wg0", false)]
    [InlineData("docker0", true)]
    [InlineData("eth0", false)]
    public void IpDiscoveryKeepsTunnels(string name, bool excluded) => Assert.Equal(excluded, NicFilter.IsExcludedForIps(name));

    [Theory]
    [InlineData(6u, 1, "Ethernet", "Red Hat VirtIO Ethernet Adapter", true)]
    [InlineData(6u, 1, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData(6u, 2, "Ethernet", "Intel", false)]
    [InlineData(24u, 1, "Loopback Pseudo-Interface 1", "Software Loopback", false)]
    [InlineData(71u, 1, "Wi-Fi", "Intel Wireless", true)]
    [InlineData(6u, 1, "Ethernet 2", "TAP-Windows Adapter V9", false)]
    public void WindowsRules(uint type, int oper, string alias, string desc, bool counted) => Assert.Equal(counted, NicFilter.IsWindowsCounted(type, oper, alias, desc));
}

public class IpRulesTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("10.0.0.5", true)]
    [InlineData("203.0.113.9", true)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::10", true)]
    [InlineData("fd7a:115c::1", true)]
    public void Reportable(string ip, bool expected) => Assert.Equal(expected, IpDiscovery.IsReportable(IPAddress.Parse(ip)));
}

public class DiskFilterTests
{
    [Theory]
    [InlineData("/", "ext4", true)]
    [InlineData("/data", "xfs", true)]
    [InlineData("/boot/efi", "vfat", false)]
    [InlineData("/snap/core/123", "squashfs", false)]
    [InlineData("/var/lib/docker/overlay2/abc", "overlay", false)]
    [InlineData("/run/user/1000", "tmpfs", false)]
    [InlineData("/proc", "proc", false)]
    [InlineData("/mnt/win", "ntfs3", true)]
    public void Counted(string mount, string fs, bool expected) => Assert.Equal(expected, DiskFilter.IsCounted(mount, fs));

    [Fact]
    public void DecodesOctalEscapes()
    {
        Assert.Equal("/mnt/my disk", DiskFilter.DecodeMountPath("/mnt/my\\040disk"));
        Assert.Equal("/plain", DiskFilter.DecodeMountPath("/plain"));
        Assert.Equal("/a\\b", DiskFilter.DecodeMountPath("/a\\b"));
    }
}
