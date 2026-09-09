using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SNM.Contracts;
using SNM.Master.Alerting;
using SNM.Master.Data;
using SNM.Master.Options;
using SNM.Master.Runtime;

namespace SNM.Master.Services;

/// <summary>Process-level facts used by /api/system/info, dashboards and install scripts.</summary>
public static class AppInfo
{
    public static readonly DateTime StartedAt = DateTime.UtcNow;

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

    public static string ShortVersion => Version.Split('+')[0];

    public static long UptimeSec => (long)(DateTime.UtcNow - StartedAt).TotalSeconds;
}

/// <summary>Resolved data directory layout (docs/DATA.md 1.1).</summary>
public sealed class DataPaths(string dataDir)
{
    public string DataDir { get; } = Path.GetFullPath(dataDir);
    public string DbPath => Path.Combine(DataDir, "snm.db");
    public string GeoIpDir => Path.Combine(DataDir, "geoip");
    public string BackupsDir => Path.Combine(DataDir, "backups");

    public long DbSizeBytes()
    {
        try { return File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0; } catch { return 0; }
    }

    public long WalSizeBytes()
    {
        try { var p = DbPath + "-wal"; return File.Exists(p) ? new FileInfo(p).Length : 0; } catch { return 0; }
    }
}

/// <summary>GET /api/dashboard/summary (docs/API.md 3).</summary>
public sealed class DashboardService(IDbContextFactory<SnmDbContext> dbFactory, NodeRegistry registry, SettingsService settings, AlertEngine alerts, GeoIpService geoIp, DataPaths paths)
{
    public async Task<object> SummaryAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var s = settings.Snapshot;
        var nodes = registry.All.ToList();
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, s.TimeZone));

        var enabled = nodes.Where(n => n.Meta.Enabled).ToList();
        var nodeCounts = new
        {
            total = nodes.Count,
            online = enabled.Count(n => n.Status == NodeStatus.Online),
            offline = enabled.Count(n => n.Status == NodeStatus.Offline),
            unknown = enabled.Count(n => n.Status == NodeStatus.Unknown),
            disabled = nodes.Count - enabled.Count,
        };

        var expiringItems = nodes
            .Where(n => n.Meta.ExpiresAt is not null)
            .Select(n => new { n, daysLeft = n.Meta.ExpiresAt!.Value.DayNumber - todayLocal.DayNumber })
            .Where(x => x.daysLeft <= s.ExpiryDays)
            .OrderBy(x => x.daysLeft)
            .Select(x => new
            {
                id = x.n.Id, publicName = x.n.Meta.PublicName, expiresAt = x.n.Meta.ExpiresAt, daysLeft = x.daysLeft,
                vendor = x.n.Meta.Vendor, price = x.n.Meta.Price, currency = x.n.Meta.Currency, billingCycleMonths = x.n.Meta.BillingCycleMonths,
            })
            .ToList();

        var baseRate = s.Rates.GetValueOrDefault(s.BaseCurrency, 1m);
        if (baseRate <= 0) baseRate = 1m;
        decimal mrrBase = 0;
        var byCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var nodesWithPrice = 0;
        foreach (var n in nodes)
        {
            var m = n.Meta;
            if (m.Price is not { } price || price <= 0 || m.BillingCycleMonths <= 0 || string.IsNullOrEmpty(m.Currency)) continue;
            nodesWithPrice++;
            var monthly = price / m.BillingCycleMonths;
            byCurrency[m.Currency] = byCurrency.GetValueOrDefault(m.Currency) + monthly;
            var rate = s.Rates.GetValueOrDefault(m.Currency, 0m);
            if (rate > 0) mrrBase += monthly * rate / baseRate;
        }

        long billedTotal = 0, limitTotal = 0; var overWarn = 0;
        foreach (var n in enabled)
        {
            var m = n.Meta;
            var billed = BillingPeriod.Billed(n.Traffic.PerRx, n.Traffic.PerTx, m.TrafficCountMode);
            billedTotal += billed;
            limitTotal += m.TrafficLimitBytes;
            if (m.TrafficLimitBytes > 0 && billed * 100.0 / m.TrafficLimitBytes >= (m.TrafficAlertPct ?? s.TrafficWarnPct)) overWarn++;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var last24h = await db.AlertEvents.CountAsync(x => x.StartedAt >= now.AddHours(-24), ct);
        var recent = await db.AlertEvents.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(10).ToListAsync(ct);

        return new
        {
            nodes = nodeCounts,
            alerts = new { firing = alerts.FiringCount, last24h },
            expiring = new
            {
                withinDays = s.ExpiryDays,
                count = expiringItems.Count(x => x.daysLeft >= 0),
                expired = expiringItems.Count(x => x.daysLeft < 0),
                items = expiringItems,
            },
            finance = new
            {
                baseCurrency = s.BaseCurrency,
                mrrBase = decimal.Round(mrrBase, 2),
                byCurrency = byCurrency.OrderBy(kv => kv.Key).Select(kv => new { currency = kv.Key, monthly = decimal.Round(kv.Value, 2) }).ToArray(),
                nodesWithPrice,
            },
            traffic = new { periodBilledBytes = billedTotal, periodLimitBytes = limitTotal, nodesOverWarn = overWarn },
            recentAlerts = recent.Select(e => new
            {
                id = e.Id, nodeId = e.NodeId, nodeName = e.NodeName, rule = e.Rule, ruleName = AlertTexts.RuleName(e.Rule), status = e.Status, severity = e.Severity,
                title = e.Title, startedAt = e.StartedAt, resolvedAt = e.ResolvedAt,
            }).ToArray(),
            system = new
            {
                version = AppInfo.ShortVersion,
                protocolVersion = ProtocolConstants.ProtocolVersion,
                startedAt = AppInfo.StartedAt,
                uptimeSec = AppInfo.UptimeSec,
                dbSizeBytes = paths.DbSizeBytes(),
                geoip = new { ready = geoIp.Ready, lastRefreshUtc = geoIp.LastRefreshUtc, ipv4Rows = geoIp.Ipv4Rows, ipv6Rows = geoIp.Ipv6Rows },
            },
        };
    }
}
