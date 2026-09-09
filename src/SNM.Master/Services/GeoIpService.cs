using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SNM.Master.Options;

namespace SNM.Master.Services;

/// <summary>Offline GeoIP (docs/DATA.md 7): ip-location-db asn-country CSVs loaded into sorted arrays, binary search lookup.</summary>
public sealed class GeoIpService(IOptions<SnmOptions> options, IHttpClientFactory httpFactory, SettingsService settings, ILogger<GeoIpService> logger)
{
    private sealed class Table<T>(T[] starts, T[] ends, string[] cc) where T : struct, IComparable<T>
    {
        public int Count => starts.Length;

        public string? Lookup(T value)
        {
            var idx = Array.BinarySearch(starts, value);
            if (idx < 0) idx = ~idx - 1;             // last start <= value
            if (idx < 0) return null;
            return ends[idx].CompareTo(value) >= 0 ? cc[idx] : null;
        }
    }

    private volatile Table<uint>? _v4;
    private volatile Table<UInt128>? _v6;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public bool Enabled => options.Value.GeoIp.Enabled;
    public bool Ready => _v4 is not null;
    public int Ipv4Rows => _v4?.Count ?? 0;
    public int Ipv6Rows => _v6?.Count ?? 0;
    public DateTime? LastRefreshUtc { get; private set; }
    public string? LastError { get; private set; }

    private string GeoDir => Path.Combine(options.Value.DataDir, "geoip");
    private string V4Path => Path.Combine(GeoDir, "asn-country-ipv4-num.csv");
    private string V6Path => Path.Combine(GeoDir, "asn-country-ipv6-num.csv");
    private string MetaPath => Path.Combine(GeoDir, "meta.json");

    /// <summary>Returns the upper-case ISO country code, or null when unknown / private / not loaded.</summary>
    public string? Lookup(IPAddress? ip)
    {
        if (ip is null || !IsPublic(ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            var v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            return _v4?.Lookup(v);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            var hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(0, 8));
            var lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(8, 8));
            return _v6?.Lookup(new UInt128(hi, lo));
        }
        return null;
    }

    /// <summary>Public routable address test (docs/PROTOCOL.md R8): excludes RFC1918, CGNAT, loopback, link-local, ULA, unspecified, multicast.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && (b[1] & 0xC0) == 64)   // 100.64.0.0/10 CGNAT
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 0
                || b[0] >= 224);                          // multicast / reserved
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Teredo) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;      // fc00::/7
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32 documentation
            return true;
        }
        return false;
    }

    public async Task TryLoadFromDiskAsync(CancellationToken ct)
    {
        if (!options.Value.GeoIp.Enabled) return;
        try
        {
            if (!File.Exists(V4Path)) return;
            await LoadAsync(ct);
            if (File.Exists(MetaPath))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(MetaPath, ct));
                if (doc.RootElement.TryGetProperty("downloadedAtUtc", out var d) && DateTime.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                    LastRefreshUtc = dt;
            }
            logger.LogInformation("GeoIP loaded from disk: {V4} IPv4 ranges, {V6} IPv6 ranges", Ipv4Rows, Ipv6Rows);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.LogWarning(ex, "GeoIP data on disk could not be loaded");
        }
    }

    public bool NeedsRefresh() => !Ready || LastRefreshUtc is null || (DateTime.UtcNow - LastRefreshUtc.Value).TotalDays >= Math.Max(1, options.Value.GeoIp.RefreshDays);

    /// <summary>Downloads both CSVs to temp files, validates, atomically replaces and hot-swaps the tables. Throws on failure.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!options.Value.GeoIp.Enabled) throw new InvalidOperationException("GeoIP 已在配置中禁用");
        await _refreshGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(GeoDir);
            var http = httpFactory.CreateClient("geoip");
            var baseUrl = options.Value.GeoIp.BaseUrl.TrimEnd('/');
            var v4Tmp = V4Path + ".tmp";
            var v6Tmp = V6Path + ".tmp";
            await DownloadAsync(http, $"{baseUrl}/asn-country-ipv4-num.csv", v4Tmp, ct);
            await DownloadAsync(http, $"{baseUrl}/asn-country-ipv6-num.csv", v6Tmp, ct);
            var (v4, v6) = (ParseV4(v4Tmp), ParseV6(v6Tmp));
            if (v4.Count < 100_000) throw new InvalidDataException($"IPv4 数据集行数异常({v4.Count})");
            if (v6.Count < 10_000) throw new InvalidDataException($"IPv6 数据集行数异常({v6.Count})");
            File.Move(v4Tmp, V4Path, overwrite: true);
            File.Move(v6Tmp, V6Path, overwrite: true);
            _v4 = v4; _v6 = v6;
            LastRefreshUtc = DateTime.UtcNow;
            LastError = null;
            await File.WriteAllTextAsync(MetaPath, JsonSerializer.Serialize(new { downloadedAtUtc = LastRefreshUtc.Value.ToString("O"), ipv4Rows = v4.Count, ipv6Rows = v6.Count }), ct);
            await PublishStatusAsync(ct);
            logger.LogInformation("GeoIP refreshed: {V4} IPv4 ranges, {V6} IPv6 ranges", v4.Count, v6.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            logger.LogWarning(ex, "GeoIP refresh failed");
            await PublishStatusAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task PublishStatusAsync(CancellationToken ct)
    {
        try
        {
            await settings.SetInternalAsync(new Dictionary<string, JsonElement>
            {
                ["geoip.lastRefreshUtc"] = SettingsService.J(LastRefreshUtc?.ToString("O") ?? ""),
                ["geoip.ipv4Rows"] = SettingsService.J(Ipv4Rows),
                ["geoip.ipv6Rows"] = SettingsService.J(Ipv6Rows),
                ["geoip.lastError"] = SettingsService.J(LastError ?? ""),
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "could not persist geoip status");
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string target, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await resp.Content.CopyToAsync(fs, ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var v4 = await Task.Run(() => ParseV4(V4Path), ct);
        var v6 = File.Exists(V6Path) ? await Task.Run(() => ParseV6(V6Path), ct) : new Table<UInt128>([], [], []);
        _v4 = v4; _v6 = v6;
    }

    private static Table<uint> ParseV4(string path)
    {
        var starts = new List<uint>(300_000); var ends = new List<uint>(300_000); var cc = new List<string>(300_000);
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var span = line.AsSpan();
            var c1 = span.IndexOf(','); if (c1 < 0) continue;
            var rest = span[(c1 + 1)..];
            var c2 = rest.IndexOf(','); if (c2 < 0) continue;
            if (!uint.TryParse(span[..c1], NumberStyles.None, CultureInfo.InvariantCulture, out var s)) continue;
            if (!uint.TryParse(rest[..c2], NumberStyles.None, CultureInfo.InvariantCulture, out var e)) continue;
            var code = rest[(c2 + 1)..].Trim().ToString().ToUpperInvariant();
            if (code.Length != 2) continue;
            if (!pool.TryGetValue(code, out var pooled)) { pooled = code; pool[code] = code; }
            starts.Add(s); ends.Add(e); cc.Add(pooled);
        }
        EnsureSorted(starts);
        return new Table<uint>(starts.ToArray(), ends.ToArray(), cc.ToArray());
    }

    private static Table<UInt128> ParseV6(string path)
    {
        var starts = new List<UInt128>(200_000); var ends = new List<UInt128>(200_000); var cc = new List<string>(200_000);
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var span = line.AsSpan();
            var c1 = span.IndexOf(','); if (c1 < 0) continue;
            var rest = span[(c1 + 1)..];
            var c2 = rest.IndexOf(','); if (c2 < 0) continue;
            if (!UInt128.TryParse(span[..c1], NumberStyles.None, CultureInfo.InvariantCulture, out var s)) continue;
            if (!UInt128.TryParse(rest[..c2], NumberStyles.None, CultureInfo.InvariantCulture, out var e)) continue;
            var code = rest[(c2 + 1)..].Trim().ToString().ToUpperInvariant();
            if (code.Length != 2) continue;
            if (!pool.TryGetValue(code, out var pooled)) { pooled = code; pool[code] = code; }
            starts.Add(s); ends.Add(e); cc.Add(pooled);
        }
        EnsureSorted(starts);
        return new Table<UInt128>(starts.ToArray(), ends.ToArray(), cc.ToArray());
    }

    private static void EnsureSorted<T>(List<T> starts) where T : IComparable<T>
    {
        for (var i = 1; i < starts.Count; i++)
        {
            if (starts[i - 1].CompareTo(starts[i]) > 0) throw new InvalidDataException("GeoIP 数据集未按起始地址排序");
        }
    }
}
