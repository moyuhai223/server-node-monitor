using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SNM.Master.Options;

namespace SNM.Master.Services;

/// <summary>Offline country lookup using numeric CSV ranges and an atomically published cache generation.</summary>
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

    private sealed record Snapshot(Table<uint> V4, Table<UInt128> V6, DateTime? DownloadedAtUtc, string? Source);
    private volatile Snapshot? _snapshot;
    private string? _generation;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public bool Enabled => options.Value.GeoIp.Enabled;
    public bool Ready => _snapshot is not null;
    public int Ipv4Rows => _snapshot?.V4.Count ?? 0;
    public int Ipv6Rows => _snapshot?.V6.Count ?? 0;
    public DateTime? LastRefreshUtc => _snapshot?.DownloadedAtUtc;
    public string? LastError { get; private set; }
    public string Dataset => options.Value.GeoIp.Dataset.Trim();
    public string BaseUrl
    {
        get
        {
            var url = options.Value.GeoIp.BaseUrl.Trim().TrimEnd('/');
            // Old installations may explicitly retain the former default in their environment.
            return Dataset == "server-country" && url == SnmOptions.GeoIpOptions.LegacyBaseUrl
                ? SnmOptions.GeoIpOptions.DefaultBaseUrl : url;
        }
    }
    private string Source => $"{BaseUrl}/{Dataset}";

    private string GeoDir => Path.Combine(options.Value.DataDir, "geoip");
    private string MetaPath => Path.Combine(GeoDir, "meta.json");
    private string GenerationDir(string generation) => Guid.TryParseExact(generation, "N", out _)
        ? Path.Combine(GeoDir, "generation-" + generation)
        : throw new InvalidDataException("GeoIP 缓存版本标识无效");

    /// <summary>Returns the upper-case ISO country code, or null when unknown / private / not loaded.</summary>
    public string? Lookup(IPAddress? ip)
    {
        if (ip is null || !IsPublic(ip)) return null;
        var snapshot = _snapshot;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            var v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            return snapshot?.V4.Lookup(v);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            var hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(0, 8));
            var lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(8, 8));
            return snapshot?.V6.Lookup(new UInt128(hi, lo));
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
        await _refreshGate.WaitAsync(ct);
        try
        {
            DateTime? downloadedAt = null;
            string? generation = null, source = null;
            if (File.Exists(MetaPath))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(MetaPath, ct));
                if (doc.RootElement.TryGetProperty("downloadedAtUtc", out var d) && DateTime.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                    downloadedAt = dt;
                if (doc.RootElement.TryGetProperty("generation", out var g)) generation = g.GetString();
                if (doc.RootElement.TryGetProperty("source", out var s)) source = s.GetString();
            }
            var dir = generation is null ? GeoDir : GenerationDir(generation);
            var v4Path = Path.Combine(dir, generation is null ? "asn-country-ipv4-num.csv" : "ipv4.csv");
            var v6Path = Path.Combine(dir, generation is null ? "asn-country-ipv6-num.csv" : "ipv6.csv");
            if (!File.Exists(v4Path)) return;
            var v4 = await Task.Run(() => ParseV4(v4Path), ct);
            var v6 = generation is not null || File.Exists(v6Path)
                ? await Task.Run(() => ParseV6(v6Path), ct) : new Table<UInt128>([], [], []);
            if (v4.Count == 0) throw new InvalidDataException("GeoIP IPv4 缓存为空");
            if (generation is not null && v6.Count == 0) throw new InvalidDataException("GeoIP IPv6 缓存为空");
            ct.ThrowIfCancellationRequested();
            // A legacy cache remains usable, but can never satisfy the new source identity.
            _snapshot = new(v4, v6, downloadedAt, generation is null ? null : source);
            _generation = generation;
            LastError = null;
            logger.LogInformation("GeoIP loaded from disk: {V4} IPv4 ranges, {V6} IPv6 ranges", Ipv4Rows, Ipv6Rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            logger.LogWarning(ex, "GeoIP data on disk could not be loaded");
        }
        finally { _refreshGate.Release(); }
    }

    public bool NeedsRefresh() => !Ready || _snapshot?.Source != Source || LastRefreshUtc is null
        || (DateTime.UtcNow - LastRefreshUtc.Value).TotalDays >= Math.Max(1, options.Value.GeoIp.RefreshDays);

    /// <summary>Downloads both CSVs to temp files, validates, atomically replaces and hot-swaps the tables. Throws on failure.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!options.Value.GeoIp.Enabled) throw new InvalidOperationException("GeoIP 已在配置中禁用");
        await _refreshGate.WaitAsync(ct);
        string? stagingDir = null;
        var metaTmp = MetaPath + ".tmp";
        try
        {
            if (Dataset.Length == 0 || Dataset.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
                throw new InvalidOperationException("GeoIP Dataset 只能包含小写字母、数字和连字符");
            Directory.CreateDirectory(GeoDir);
            var http = httpFactory.CreateClient("geoip");
            var generation = Guid.NewGuid().ToString("N");
            stagingDir = GenerationDir(generation);
            Directory.CreateDirectory(stagingDir);
            var v4Tmp = Path.Combine(stagingDir, "ipv4.csv");
            var v6Tmp = Path.Combine(stagingDir, "ipv6.csv");
            await DownloadAsync(http, $"{Source}-ipv4-num.csv", v4Tmp, ct);
            await DownloadAsync(http, $"{Source}-ipv6-num.csv", v6Tmp, ct);
            var (v4, v6) = (ParseV4(v4Tmp), ParseV6(v6Tmp));
            if (v4.Count < 100_000) throw new InvalidDataException($"IPv4 数据集行数异常({v4.Count})");
            if (v6.Count < 10_000) throw new InvalidDataException($"IPv6 数据集行数异常({v6.Count})");
            var snapshot = new Snapshot(v4, v6, DateTime.UtcNow, Source);
            await File.WriteAllTextAsync(metaTmp, JsonSerializer.Serialize(new
            {
                generation, source = snapshot.Source, downloadedAtUtc = snapshot.DownloadedAtUtc!.Value.ToString("O"),
                ipv4Rows = v4.Count, ipv6Rows = v6.Count,
            }), ct);
            ct.ThrowIfCancellationRequested();
            // This one rename commits both complete files. Before it, the old manifest
            // and in-memory tables remain intact even if the second download fails.
            File.Move(metaTmp, MetaPath, overwrite: true);
            var previous = _generation;
            _generation = generation;
            _snapshot = snapshot;
            stagingDir = null;
            LastError = null;
            if (previous is not null) DeleteGeneration(GenerationDir(previous));
            await PublishStatusAsync(ct);
            logger.LogInformation("GeoIP refreshed: {V4} IPv4 ranges, {V6} IPv6 ranges", v4.Count, v6.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            logger.LogWarning(ex, "GeoIP refresh failed");
            await PublishStatusAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (stagingDir is not null) DeleteGeneration(stagingDir);
            try { File.Delete(metaTmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _refreshGate.Release();
        }
    }

    private void DeleteGeneration(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogDebug(ex, "Could not remove unused GeoIP cache {Directory}", dir); }
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

    private static Table<uint> ParseV4(string path)
    {
        var starts = new List<uint>(300_000); var ends = new List<uint>(300_000); var cc = new List<string>(300_000);
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var span = line.AsSpan();
            if (span.IsWhiteSpace()) continue;
            var c1 = span.IndexOf(','); if (c1 < 0) throw new InvalidDataException("GeoIP CSV 格式无效");
            var rest = span[(c1 + 1)..];
            var c2 = rest.IndexOf(','); if (c2 < 0) throw new InvalidDataException("GeoIP CSV 格式无效");
            if (!uint.TryParse(span[..c1], NumberStyles.None, CultureInfo.InvariantCulture, out var s)) throw new InvalidDataException("GeoIP IPv4 起始地址无效");
            if (!uint.TryParse(rest[..c2], NumberStyles.None, CultureInfo.InvariantCulture, out var e)) throw new InvalidDataException("GeoIP IPv4 结束地址无效");
            var code = rest[(c2 + 1)..].Trim().ToString().ToUpperInvariant();
            if (code.Length != 2 || code.Any(c => c is < 'A' or > 'Z')) throw new InvalidDataException("GeoIP 国家码无效");
            if (!pool.TryGetValue(code, out var pooled)) { pooled = code; pool[code] = code; }
            starts.Add(s); ends.Add(e); cc.Add(pooled);
        }
        EnsureSorted(starts, ends);
        return new Table<uint>(starts.ToArray(), ends.ToArray(), cc.ToArray());
    }

    private static Table<UInt128> ParseV6(string path)
    {
        var starts = new List<UInt128>(200_000); var ends = new List<UInt128>(200_000); var cc = new List<string>(200_000);
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var span = line.AsSpan();
            if (span.IsWhiteSpace()) continue;
            var c1 = span.IndexOf(','); if (c1 < 0) throw new InvalidDataException("GeoIP CSV 格式无效");
            var rest = span[(c1 + 1)..];
            var c2 = rest.IndexOf(','); if (c2 < 0) throw new InvalidDataException("GeoIP CSV 格式无效");
            if (!UInt128.TryParse(span[..c1], NumberStyles.None, CultureInfo.InvariantCulture, out var s)) throw new InvalidDataException("GeoIP IPv6 起始地址无效");
            if (!UInt128.TryParse(rest[..c2], NumberStyles.None, CultureInfo.InvariantCulture, out var e)) throw new InvalidDataException("GeoIP IPv6 结束地址无效");
            var code = rest[(c2 + 1)..].Trim().ToString().ToUpperInvariant();
            if (code.Length != 2 || code.Any(c => c is < 'A' or > 'Z')) throw new InvalidDataException("GeoIP 国家码无效");
            if (!pool.TryGetValue(code, out var pooled)) { pooled = code; pool[code] = code; }
            starts.Add(s); ends.Add(e); cc.Add(pooled);
        }
        EnsureSorted(starts, ends);
        return new Table<UInt128>(starts.ToArray(), ends.ToArray(), cc.ToArray());
    }

    private static void EnsureSorted<T>(List<T> starts, List<T> ends) where T : IComparable<T>
    {
        for (var i = 0; i < starts.Count; i++)
        {
            if (starts[i].CompareTo(ends[i]) > 0 || (i > 0 && ends[i - 1].CompareTo(starts[i]) >= 0))
                throw new InvalidDataException("GeoIP 地址范围无效、重叠或未排序");
        }
    }
}
