using System.Globalization;
using System.Net;
using SNM.Contracts;
using SNM.Master.Data.Entities;

namespace SNM.Master.Alerting;

/// <summary>Chinese notification texts (docs/DATA.md 5.3). Telegram gets HTML; webhooks get the plain text too.</summary>
public static class AlertTexts
{
    public static string RuleName(int rule) => rule switch
    {
        AlertRule.Offline => "离线",
        AlertRule.CpuHigh => "CPU 高负载",
        AlertRule.TrafficWarn => "流量预警",
        AlertRule.TrafficExceeded => "流量超限",
        AlertRule.Expiry => "即将到期",
        AlertRule.DiskHigh => "磁盘告急",
        _ => "告警",
    };

    public static string SeverityName(int severity) => severity switch
    {
        AlertSeverity.Critical => "critical",
        AlertSeverity.Warning => "warning",
        _ => "info",
    };

    public static string RuleSlug(int rule) => rule switch
    {
        AlertRule.Offline => "offline",
        AlertRule.CpuHigh => "cpu_high",
        AlertRule.TrafficWarn => "traffic_warn",
        AlertRule.TrafficExceeded => "traffic_exceeded",
        AlertRule.Expiry => "expiry",
        AlertRule.DiskHigh => "disk_high",
        _ => "unknown",
    };

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var i = 0;
        while (bytes >= 1000 && i < units.Length - 1) { bytes /= 1000; i++; }
        return i == 0 ? $"{bytes:F0} {units[i]}" : $"{bytes:F1} {units[i]}";
    }

    public static string FormatDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds} 秒";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} 分 {d.Seconds} 秒";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} 小时 {d.Minutes} 分";
        return $"{(int)d.TotalDays} 天 {d.Hours} 小时";
    }

    public static string FormatTime(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        var offset = tz.GetUtcOffset(utc);
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString("hh\\:mm");
    }

    /// <summary>Firing title/message.</summary>
    public static (string Title, string Message) Firing(int rule, Node node, double value, double threshold, string subject, TimeZoneInfo tz, DateTime now, string? extra)
    {
        var name = node.PublicName;
        return rule switch
        {
            AlertRule.Offline => ($"[离线] {name}",
                $"节点已离线 {(int)value} 秒(最后上报 {(node.LastSeenAt is { } ls ? FormatTime(ls, tz) : "未知")})"),
            AlertRule.CpuHigh => ($"[CPU 高负载] {name}",
                $"最近 1 分钟 CPU 均值 {value / 10:F1}%,已持续超过阈值 {threshold:F0}%"),
            AlertRule.TrafficWarn => ($"[流量预警] {name}",
                $"本账期已用 {extra}({value:F1}%),预警阈值 {threshold:F0}%"),
            AlertRule.TrafficExceeded => ($"[流量超限] {name}",
                $"本账期已用 {extra}({value:F1}%),已超过限额"),
            AlertRule.Expiry => value < 0
                ? ($"[已过期] {name}", $"已于 {node.ExpiresAt:yyyy-MM-dd} 到期,过期 {-(int)value} 天{FinanceSuffix(node)}")
                : ($"[即将到期] {name}", $"将于 {node.ExpiresAt:yyyy-MM-dd} 到期,剩余 {(int)value} 天{FinanceSuffix(node)}"),
            AlertRule.DiskHigh => ($"[磁盘告急] {name}",
                $"挂载点 {subject} 已用 {value:F1}%,阈值 {threshold:F0}%"),
            _ => ($"[告警] {name}", $"值 {value},阈值 {threshold}"),
        };
    }

    private static string FinanceSuffix(Node node)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(node.Vendor)) parts.Add($"供应商 {node.Vendor}");
        if (node.Price is { } p && !string.IsNullOrEmpty(node.Currency))
        {
            var cycle = node.BillingCycleMonths switch { 1 => "月", 3 => "季", 6 => "半年", 12 => "年", 24 => "两年", 36 => "三年", _ => "" };
            parts.Add($"续费 {p.ToString("0.##", CultureInfo.InvariantCulture)} {node.Currency}{(cycle.Length > 0 ? "/" + cycle : "")}");
        }
        return parts.Count == 0 ? "" : ";" + string.Join(",", parts);
    }

    public static (string Title, string Message) Recovery(int rule, Node node, DateTime firingSince, DateTime now, string? extra)
    {
        var name = node.PublicName;
        var dur = FormatDuration(now - firingSince);
        return rule switch
        {
            AlertRule.Offline => ($"[恢复] {name}", $"节点恢复在线,离线时长 {dur}"),
            AlertRule.CpuHigh => ($"[恢复] {name}", $"CPU 负载已回落,高负载持续 {dur}"),
            AlertRule.TrafficWarn or AlertRule.TrafficExceeded => ($"[恢复] {name}", $"流量比例已回落至阈值以下{(extra is null ? "" : "(" + extra + ")")}"),
            AlertRule.Expiry => ($"[已续费] {name}", $"到期日已更新为 {node.ExpiresAt:yyyy-MM-dd}"),
            AlertRule.DiskHigh => ($"[恢复] {name}", $"磁盘空间已回落,告急持续 {dur}"),
            _ => ($"[恢复] {name}", $"告警已恢复,持续 {dur}"),
        };
    }

    /// <summary>Telegram HTML message body.</summary>
    public static string TelegramHtml(AlertEvent ev, Node? node, TimeZoneInfo tz, string siteTitle)
    {
        var icon = ev.Status == AlertEventStatus.Resolved ? "🟢" : ev.Severity == AlertSeverity.Critical ? "🔴" : "🟠";
        var lines = new List<string>
        {
            $"{icon} <b>{WebUtility.HtmlEncode(ev.Title)}</b>",
            WebUtility.HtmlEncode(ev.Message),
        };
        if (!string.IsNullOrEmpty(node?.AdminRemark)) lines.Add($"备注:{WebUtility.HtmlEncode(node.AdminRemark)}");
        lines.Add($"时间:{FormatTime(ev.ResolvedAt ?? ev.StartedAt, tz)}");
        lines.Add($"<i>{WebUtility.HtmlEncode(siteTitle)}</i>");
        return string.Join("\n", lines);
    }

    public static string PlainText(AlertEvent ev, Node? node, TimeZoneInfo tz)
    {
        var icon = ev.Status == AlertEventStatus.Resolved ? "🟢" : ev.Severity == AlertSeverity.Critical ? "🔴" : "🟠";
        var lines = new List<string> { $"{icon} {ev.Title}", ev.Message };
        if (!string.IsNullOrEmpty(node?.AdminRemark)) lines.Add($"备注:{node.AdminRemark}");
        lines.Add($"时间:{FormatTime(ev.ResolvedAt ?? ev.StartedAt, tz)}");
        return string.Join("\n", lines);
    }
}
