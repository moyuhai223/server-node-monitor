using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Options;

namespace SNM.Master.Services;

/// <summary>Strongly typed view of the runtime settings, rebuilt whenever a key changes. Hot paths read this.</summary>
public sealed class SettingsSnapshot
{
    public string SiteTitle = "Server Node Monitor";
    public string PublicTitle = "节点状态";
    public string PublicSubtitle = "";
    public string PublicBaseUrl = "";
    public string TimeZoneId = "UTC";
    public TimeZoneInfo TimeZone = TimeZoneInfo.Utc;
    public bool ShowSpecs = true;
    public bool ShowTraffic = true;
    public string Theme = "default";
    public string ThemeOptions = "";
    public string ReleaseBaseUrl = "";
    public int DefaultIntervalMs = 2000;
    public int StatusIntervalSec = 300;
    public int InstallTokenTtlHours = 24;
    public bool AlertEnabled = true;
    public int OfflineTimeoutSec = 30;
    public int OfflineConsecutive = 2;
    public int CpuPct = 90;
    public int CpuSustainMin = 5;
    public int TrafficWarnPct = 80;
    public int ExpiryDays = 7;
    public int ExpiryCheckHour = 9;
    public int CooldownMin = 30;
    public int RepeatMin;
    public bool DiskEnabled;
    public int DiskPct = 90;
    public string BaseCurrency = "CNY";
    public Dictionary<string, decimal> Rates = new() { ["CNY"] = 1m, ["USD"] = 7.2m, ["EUR"] = 7.8m };
    public int AccessTokenMinutes = 120;
    public int RefreshTokenDays = 30;
    public int LoginMaxFailures = 5;
    public int LoginLockMinutes = 15;
    public bool GeoIpEnabled = true;
    public int RetentionAlertEventDays = 180;
    public int RetentionDeliveryDays = 30;
    public int RetentionTrafficDailyDays = 400;
}

public sealed record SettingError(string Key, string Message);

/// <summary>Key/value settings stored in the Settings table (docs/DATA.md 6) with an in-memory cache and validation.</summary>
public sealed class SettingsService(IDbContextFactory<SnmDbContext> dbFactory, ILogger<SettingsService> logger)
{
    public const string JwtSecretKey = "auth.jwtSecret";

    private sealed record Def(string Default, Func<JsonElement, string?> Validate, bool Secret = false, bool ReadOnly = false);

    private static readonly Regex TwoLetterUpper = new("^[A-Z]{2}$", RegexOptions.Compiled);

    private static Func<JsonElement, string?> Str(int min, int max) => e =>
        e.ValueKind != JsonValueKind.String ? "必须是字符串" : e.GetString()!.Length < min || e.GetString()!.Length > max ? $"长度须在 {min}–{max} 之间" : null;

    private static Func<JsonElement, string?> Int(int min, int max) => e =>
        e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var v) ? "必须是整数" : v < min || v > max ? $"须在 {min}–{max} 之间" : null;

    private static Func<JsonElement, string?> IntZeroOr(int min, int max) => e =>
        e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var v) ? "必须是整数" : v != 0 && (v < min || v > max) ? $"须为 0 或在 {min}–{max} 之间" : null;

    private static readonly Func<JsonElement, string?> Bool = e => e.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "必须是布尔值";

    private static Func<JsonElement, string?> OneOf(params string[] allowed) => e =>
        e.ValueKind != JsonValueKind.String || !allowed.Contains(e.GetString()) ? $"须为 {string.Join('/', allowed)} 之一" : null;

    private static readonly Func<JsonElement, string?> HttpUrlOrEmpty = e =>
    {
        if (e.ValueKind != JsonValueKind.String) return "必须是字符串";
        var s = e.GetString()!;
        if (s.Length == 0) return null;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https") ? null : "须为 http(s) 地址";
    };

    private static readonly Func<JsonElement, string?> TimeZoneId = e =>
        e.ValueKind != JsonValueKind.String ? "必须是字符串" : TimeZoneInfo.TryFindSystemTimeZoneById(e.GetString()!, out _) ? null : "无效的时区标识";

    private static readonly Func<JsonElement, string?> Rates = e =>
    {
        if (e.ValueKind != JsonValueKind.Object) return "必须是对象";
        foreach (var cur in new[] { "CNY", "USD", "EUR" })
        {
            if (!e.TryGetProperty(cur, out var v) || v.ValueKind != JsonValueKind.Number || v.GetDecimal() <= 0) return $"{cur} 汇率须为正数";
        }
        return null;
    };

    private static readonly Func<JsonElement, string?> Any = _ => null;

    private static readonly Func<JsonElement, string?> JsonTextOrEmpty = e =>
    {
        if (e.ValueKind != JsonValueKind.String) return "必须是字符串";
        var s = e.GetString()!;
        if (s.Length == 0) return null;
        if (s.Length > 8000) return "长度不能超过 8000";
        try { using var doc = JsonDocument.Parse(s); return doc.RootElement.ValueKind == JsonValueKind.Object ? null : "必须是 JSON 对象"; }
        catch (JsonException) { return "不是合法的 JSON"; }
    };

    private static readonly IReadOnlyDictionary<string, Def> Defs = new Dictionary<string, Def>
    {
        ["site.title"] = new("\"Server Node Monitor\"", Str(1, 64)),
        ["site.publicTitle"] = new("\"节点状态\"", Str(1, 64)),
        ["site.publicSubtitle"] = new("\"\"", Str(0, 128)),
        ["site.publicBaseUrl"] = new("\"\"", HttpUrlOrEmpty),
        ["site.timeZone"] = new("\"UTC\"", TimeZoneId),
        ["public.showSpecs"] = new("true", Bool),
        ["public.showTraffic"] = new("true", Bool),
        ["site.theme"] = new("\"default\"", Str(2, 32)),
        ["site.themeOptions"] = new("\"\"", JsonTextOrEmpty),
        ["agent.releaseBaseUrl"] = new("\"https://github.com/moyuhai223/server-node-monitor/releases/latest/download\"", HttpUrlOrEmpty),
        ["agent.defaultIntervalMs"] = new("2000", Int(1000, 60000)),
        ["agent.statusIntervalSec"] = new("300", Int(60, 3600)),
        ["agent.installTokenTtlHours"] = new("24", Int(1, 168)),
        ["alert.enabled"] = new("true", Bool),
        ["alert.offlineTimeoutSec"] = new("30", Int(10, 600)),
        ["alert.offlineConsecutive"] = new("2", Int(1, 10)),
        ["alert.cpuPct"] = new("90", Int(50, 100)),
        ["alert.cpuSustainMin"] = new("5", Int(1, 60)),
        ["alert.trafficWarnPct"] = new("80", Int(50, 99)),
        ["alert.expiryDays"] = new("7", Int(1, 60)),
        ["alert.expiryCheckHour"] = new("9", Int(0, 23)),
        ["alert.cooldownMin"] = new("30", Int(30, 60)),
        ["alert.repeatMin"] = new("0", IntZeroOr(30, 1440)),
        ["alert.diskEnabled"] = new("false", Bool),
        ["alert.diskPct"] = new("90", Int(50, 100)),
        ["alert.lastExpiryCheckDate"] = new("\"\"", Any, ReadOnly: true),
        ["finance.baseCurrency"] = new("\"CNY\"", OneOf("USD", "CNY", "EUR")),
        ["finance.rates"] = new("{\"CNY\":1,\"USD\":7.2,\"EUR\":7.8}", Rates),
        [JwtSecretKey] = new("\"\"", Any, Secret: true, ReadOnly: true),
        ["auth.accessTokenMinutes"] = new("120", Int(5, 1440)),
        ["auth.refreshTokenDays"] = new("30", Int(1, 365)),
        ["auth.loginMaxFailures"] = new("5", Int(3, 20)),
        ["auth.loginLockMinutes"] = new("15", Int(1, 1440)),
        ["geoip.enabled"] = new("true", Bool),
        ["geoip.lastRefreshUtc"] = new("\"\"", Any, ReadOnly: true),
        ["geoip.ipv4Rows"] = new("0", Any, ReadOnly: true),
        ["geoip.ipv6Rows"] = new("0", Any, ReadOnly: true),
        ["geoip.lastError"] = new("\"\"", Any, ReadOnly: true),
        ["retention.alertEventDays"] = new("180", Int(7, 3650)),
        ["retention.deliveryDays"] = new("30", Int(7, 365)),
        ["retention.trafficDailyDays"] = new("400", Int(31, 3650)),
    };

    public static IEnumerable<string> Keys => Defs.Keys;
    public static bool IsSecret(string key) => Defs.TryGetValue(key, out var d) && d.Secret;
    public static bool IsReadOnly(string key) => Defs.TryGetValue(key, out var d) && d.ReadOnly;

    private readonly ConcurrentDictionary<string, JsonElement> _cache = new(StringComparer.Ordinal);
    private volatile SettingsSnapshot _snapshot = new();

    public SettingsSnapshot Snapshot => _snapshot;

    /// <summary>Raised after a successful UpdateAsync/SetInternalAsync with the changed keys.</summary>
    public event Action<IReadOnlyCollection<string>>? Changed;

    /// <summary>Loads all keys, fills missing defaults, seeds site.timeZone / site.publicBaseUrl / auth.jwtSecret from options.</summary>
    public async Task InitializeAsync(SnmOptions opts, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Settings.ToDictionaryAsync(x => x.Key, ct);
        var now = DateTime.UtcNow;
        var added = 0;

        foreach (var (key, def) in Defs)
        {
            if (rows.TryGetValue(key, out var row) && TryParse(row.Value, out var el))
            {
                _cache[key] = el;
                continue;
            }
            var value = def.Default;
            if (key == "site.timeZone") value = JsonSerializer.Serialize(DefaultTimeZoneId(opts.TimeZone));
            if (key == "site.publicBaseUrl" && !string.IsNullOrWhiteSpace(opts.PublicBaseUrl)) value = JsonSerializer.Serialize(opts.PublicBaseUrl.TrimEnd('/'));
            if (key == JwtSecretKey) value = JsonSerializer.Serialize(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
            if (row is null) db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = now });
            else { row.Value = value; row.UpdatedAt = now; }
            _cache[key] = JsonDocument.Parse(value).RootElement.Clone();
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} default settings", added);
        }

        Rebuild();
    }

    private static string DefaultTimeZoneId(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && TimeZoneInfo.TryFindSystemTimeZoneById(configured, out _)) return configured;
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "UTC";
    }

    private static bool TryParse(string json, out JsonElement el)
    {
        try { el = JsonDocument.Parse(json).RootElement.Clone(); return true; }
        catch (JsonException) { el = default; return false; }
    }

    public JsonElement GetRaw(string key) => _cache.TryGetValue(key, out var e) ? e : JsonDocument.Parse(Defs[key].Default).RootElement.Clone();
    public string GetString(string key) => GetRaw(key) is { ValueKind: JsonValueKind.String } e ? e.GetString()! : "";
    public int GetInt(string key) => GetRaw(key) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var v) ? v : 0;
    public bool GetBool(string key) => GetRaw(key).ValueKind == JsonValueKind.True;

    /// <summary>Effective JWT secret: SNM_JWT_SECRET (options) wins over the stored value.</summary>
    public string ResolveJwtSecret(SnmOptions opts) => !string.IsNullOrWhiteSpace(opts.Jwt.Secret) && opts.Jwt.Secret.Length >= 32 ? opts.Jwt.Secret : GetString(JwtSecretKey);

    /// <summary>Validates and persists a batch of user-editable keys. Returns errors (empty = applied).</summary>
    public async Task<IReadOnlyList<SettingError>> UpdateAsync(IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct = default)
    {
        var errors = new List<SettingError>();
        foreach (var (key, value) in values)
        {
            if (!Defs.TryGetValue(key, out var def)) { errors.Add(new(key, "未知的设置项")); continue; }
            if (def.ReadOnly || def.Secret) { errors.Add(new(key, "该项只读")); continue; }
            var err = def.Validate(value);
            if (err is not null) errors.Add(new(key, err));
        }
        if (errors.Count > 0) return errors;

        await SetInternalAsync(values, ct);
        return errors;
    }

    /// <summary>Writes keys without validation (internal state such as geoip.* or alert.lastExpiryCheckDate).</summary>
    public async Task SetInternalAsync(IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct = default)
    {
        if (values.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var keys = values.Keys.ToArray();
        var rows = await db.Settings.Where(x => keys.Contains(x.Key)).ToDictionaryAsync(x => x.Key, ct);
        foreach (var (key, value) in values)
        {
            var json = value.GetRawText();
            if (rows.TryGetValue(key, out var row)) { row.Value = json; row.UpdatedAt = now; }
            else db.Settings.Add(new Setting { Key = key, Value = json, UpdatedAt = now });
            _cache[key] = value.Clone();
        }
        await db.SaveChangesAsync(ct);
        Rebuild();
        try { Changed?.Invoke(keys); }
        catch (Exception ex) { logger.LogWarning(ex, "Settings change handler failed"); }
    }

    public Task SetInternalAsync(string key, JsonElement value, CancellationToken ct = default)
        => SetInternalAsync(new Dictionary<string, JsonElement> { [key] = value }, ct);

    public static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Nested object for GET /api/settings; secrets and internal keys omitted.</summary>
    public JsonObject Export()
    {
        var root = new JsonObject();
        foreach (var key in Defs.Keys)
        {
            if (IsSecret(key)) continue;
            var dot = key.IndexOf('.');
            var group = key[..dot];
            var name = key[(dot + 1)..];
            if (root[group] is not JsonObject g) { g = new JsonObject(); root[group] = g; }
            g[name] = JsonNode.Parse(GetRaw(key).GetRawText());
        }
        return root;
    }

    /// <summary>Flattens a PATCH body ({"alert":{"cpuPct":85}}) into dotted keys.</summary>
    public static Dictionary<string, JsonElement> Flatten(JsonElement body)
    {
        var result = new Dictionary<string, JsonElement>();
        if (body.ValueKind != JsonValueKind.Object) return result;
        foreach (var group in body.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var p in group.Value.EnumerateObject()) result[$"{group.Name}.{p.Name}"] = p.Value.Clone();
        }
        return result;
    }

    private void Rebuild()
    {
        var s = new SettingsSnapshot
        {
            SiteTitle = GetString("site.title"),
            PublicTitle = GetString("site.publicTitle"),
            PublicSubtitle = GetString("site.publicSubtitle"),
            PublicBaseUrl = GetString("site.publicBaseUrl").TrimEnd('/'),
            TimeZoneId = GetString("site.timeZone"),
            ShowSpecs = GetBool("public.showSpecs"),
            ShowTraffic = GetBool("public.showTraffic"),
            Theme = GetString("site.theme"),
            ThemeOptions = GetString("site.themeOptions"),
            ReleaseBaseUrl = GetString("agent.releaseBaseUrl").TrimEnd('/'),
            DefaultIntervalMs = GetInt("agent.defaultIntervalMs"),
            StatusIntervalSec = GetInt("agent.statusIntervalSec"),
            InstallTokenTtlHours = GetInt("agent.installTokenTtlHours"),
            AlertEnabled = GetBool("alert.enabled"),
            OfflineTimeoutSec = GetInt("alert.offlineTimeoutSec"),
            OfflineConsecutive = GetInt("alert.offlineConsecutive"),
            CpuPct = GetInt("alert.cpuPct"),
            CpuSustainMin = GetInt("alert.cpuSustainMin"),
            TrafficWarnPct = GetInt("alert.trafficWarnPct"),
            ExpiryDays = GetInt("alert.expiryDays"),
            ExpiryCheckHour = GetInt("alert.expiryCheckHour"),
            CooldownMin = GetInt("alert.cooldownMin"),
            RepeatMin = GetInt("alert.repeatMin"),
            DiskEnabled = GetBool("alert.diskEnabled"),
            DiskPct = GetInt("alert.diskPct"),
            BaseCurrency = GetString("finance.baseCurrency"),
            AccessTokenMinutes = GetInt("auth.accessTokenMinutes"),
            RefreshTokenDays = GetInt("auth.refreshTokenDays"),
            LoginMaxFailures = GetInt("auth.loginMaxFailures"),
            LoginLockMinutes = GetInt("auth.loginLockMinutes"),
            GeoIpEnabled = GetBool("geoip.enabled"),
            RetentionAlertEventDays = GetInt("retention.alertEventDays"),
            RetentionDeliveryDays = GetInt("retention.deliveryDays"),
            RetentionTrafficDailyDays = GetInt("retention.trafficDailyDays"),
        };
        s.TimeZone = TimeZoneInfo.TryFindSystemTimeZoneById(s.TimeZoneId, out var tz) ? tz : TimeZoneInfo.Utc;
        var rates = GetRaw("finance.rates");
        if (rates.ValueKind == JsonValueKind.Object)
        {
            s.Rates = new Dictionary<string, decimal>();
            foreach (var p in rates.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number) s.Rates[p.Name] = p.Value.GetDecimal();
            }
        }
        _snapshot = s;
    }
}
