using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SNM.Contracts;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Services;

namespace SNM.Master.Alerting;

public sealed class TelegramConfig
{
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string ParseMode { get; set; } = "HTML";
    public bool DisableNotification { get; set; }
    public long? MessageThreadId { get; set; }
}

public sealed class WebhookConfig
{
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string? Secret { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? BodyTemplate { get; set; }
    public int TimeoutSec { get; set; } = 10;
}

public static class ChannelTypes
{
    public const string Telegram = "telegram";
    public const string Webhook = "webhook";
}

public sealed record DeliveryResult(bool Ok, int StatusCode, string? Error, int ElapsedMs, string? Response);

public sealed record NotificationJob(AlertEvent Event, int Kind, Node? Node);

public static class DeliveryKind
{
    public const int Firing = 1;
    public const int Recovery = 2;
    public const int Test = 3;
}

/// <summary>Delivers alert events to enabled channels with retries and records every attempt (docs/DATA.md 5.4).</summary>
public sealed class NotificationDispatcher(IDbContextFactory<SnmDbContext> dbFactory, IHttpClientFactory httpFactory, SettingsService settings, ILogger<NotificationDispatcher> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Channel<NotificationJob> _queue = Channel.CreateUnbounded<NotificationJob>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Retry back-off; shortened by tests.</summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)];

    public int Pending => _queue.Reader.Count;

    public void Enqueue(NotificationJob job) => _queue.Writer.TryWrite(job);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await DeliverAsync(job, stoppingToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Delivery of event {EventId} failed unexpectedly", job.Event.Id); }
        }
    }

    private async Task DeliverAsync(NotificationJob job, CancellationToken ct)
    {
        List<NotificationChannel> channels;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            channels = await db.NotificationChannels.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
        }
        var rule = job.Event.Rule;
        var targets = channels.Where(c => (c.RuleMask == 0 || (c.RuleMask & (1 << rule)) != 0) && job.Event.Severity >= c.MinSeverity).ToList();
        if (targets.Count == 0) return;

        var tasks = targets.Select(c => DeliverToChannelAsync(c, job, ct));
        await Task.WhenAll(tasks);
    }

    private async Task DeliverToChannelAsync(NotificationChannel channel, NotificationJob job, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= RetryDelays.Length; attempt++)
        {
            var result = await SendAsync(channel, job.Event, job.Node, ct);
            await RecordAsync(channel, job.Event.Id, job.Kind, attempt, result, ct);
            if (result.Ok)
            {
                logger.LogInformation("Alert {EventId} delivered via {Channel} ({Type}) in {Elapsed} ms", job.Event.Id, channel.Name, channel.Type, result.ElapsedMs);
                return;
            }
            logger.LogWarning("Alert {EventId} delivery via {Channel} failed (attempt {Attempt}): {Error}", job.Event.Id, channel.Name, attempt, result.Error);
            if (attempt < RetryDelays.Length) await Task.Delay(RetryDelays[attempt - 1], ct);
        }
    }

    private async Task RecordAsync(NotificationChannel channel, long? eventId, int kind, int attempt, DeliveryResult r, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                EventId = eventId, ChannelId = channel.Id, ChannelName = channel.Name, Kind = kind, Attempt = attempt,
                Ok = r.Ok, StatusCode = r.StatusCode, Error = r.Error is null ? null : (r.Error.Length > 500 ? r.Error[..500] : r.Error),
                ElapsedMs = r.ElapsedMs, CreatedAt = DateTime.UtcNow,
            });
            var now = DateTime.UtcNow;
            var err = r.Ok ? null : (r.Error is null ? null : (r.Error.Length > 500 ? r.Error[..500] : r.Error));
            await db.NotificationChannels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LastSuccessAt, c => r.Ok ? now : c.LastSuccessAt)
                .SetProperty(c => c.LastError, err), ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "could not record delivery");
        }
    }

    /// <summary>Sends a synthetic test event through the channel (used by the API); records a delivery when the channel is saved (Id > 0).</summary>
    public async Task<DeliveryResult> TestAsync(NotificationChannel channel, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ev = new AlertEvent
        {
            Id = 0, NodeId = null, NodeName = "TEST", Rule = AlertRule.Offline, Subject = "", Status = AlertEventStatus.Firing, Severity = AlertSeverity.Info,
            Title = "[测试] Server Node Monitor", Message = $"这是一条来自 {settings.Snapshot.SiteTitle} 的测试通知,渠道「{channel.Name}」配置正常。",
            Value = 0, Threshold = 0, DedupKey = "test", StartedAt = now, Notified = true,
        };
        var result = await SendAsync(channel, ev, null, ct);
        if (channel.Id > 0)
        {
            await RecordAsync(channel, null, DeliveryKind.Test, 1, result, ct);
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await db.NotificationChannels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastTestAt, now), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "could not record test time"); }
        }
        return result;
    }

    public async Task<DeliveryResult> SendAsync(NotificationChannel channel, AlertEvent ev, Node? node, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return channel.Type switch
            {
                ChannelTypes.Telegram => await SendTelegramAsync(channel, ev, node, sw, ct),
                ChannelTypes.Webhook => await SendWebhookAsync(channel, ev, node, sw, ct),
                _ => new DeliveryResult(false, 0, $"unknown channel type {channel.Type}", (int)sw.ElapsedMilliseconds, null),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeliveryResult(false, 0, "请求超时", (int)sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DeliveryResult(false, 0, ex.Message, (int)sw.ElapsedMilliseconds, null);
        }
    }

    private async Task<DeliveryResult> SendTelegramAsync(NotificationChannel channel, AlertEvent ev, Node? node, Stopwatch sw, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<TelegramConfig>(channel.ConfigJson, JsonOpts) ?? new TelegramConfig();
        if (string.IsNullOrWhiteSpace(cfg.BotToken) || string.IsNullOrWhiteSpace(cfg.ChatId))
            return new DeliveryResult(false, 0, "Telegram 配置缺少 botToken 或 chatId", 0, null);
        var tz = settings.Snapshot.TimeZone;
        var text = string.Equals(cfg.ParseMode, "HTML", StringComparison.OrdinalIgnoreCase)
            ? AlertTexts.TelegramHtml(ev, node, tz, settings.Snapshot.SiteTitle)
            : AlertTexts.PlainText(ev, node, tz);
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = cfg.ChatId,
            ["text"] = text,
            ["disable_web_page_preview"] = true,
            ["disable_notification"] = cfg.DisableNotification,
        };
        if (!string.IsNullOrWhiteSpace(cfg.ParseMode) && cfg.ParseMode != "none") payload["parse_mode"] = cfg.ParseMode;
        if (cfg.MessageThreadId is { } thread) payload["message_thread_id"] = thread;

        var http = httpFactory.CreateClient("notify");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await http.PostAsync($"https://api.telegram.org/bot{cfg.BotToken}/sendMessage",
            new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"), cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        var ok = resp.IsSuccessStatusCode && body.Contains("\"ok\":true", StringComparison.Ordinal);
        return new DeliveryResult(ok, (int)resp.StatusCode, ok ? null : Truncate(body), (int)sw.ElapsedMilliseconds, Truncate(body));
    }

    private async Task<DeliveryResult> SendWebhookAsync(NotificationChannel channel, AlertEvent ev, Node? node, Stopwatch sw, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<WebhookConfig>(channel.ConfigJson, JsonOpts) ?? new WebhookConfig();
        if (!Uri.TryCreate(cfg.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new DeliveryResult(false, 0, "Webhook URL 无效", 0, null);

        var s = settings.Snapshot;
        var eventName = ev.Id == 0 ? "test" : ev.Status == AlertEventStatus.Resolved ? "alert.resolved" : "alert.firing";
        var text = AlertTexts.PlainText(ev, node, s.TimeZone);
        var siteUrl = s.PublicBaseUrl.Length > 0 ? s.PublicBaseUrl + "/admin/" : "";
        string body;
        if (!string.IsNullOrWhiteSpace(cfg.BodyTemplate))
        {
            body = RenderTemplate(cfg.BodyTemplate, new Dictionary<string, string?>
            {
                ["event"] = eventName, ["id"] = ev.Id.ToString(), ["rule"] = AlertTexts.RuleSlug(ev.Rule), ["severity"] = AlertTexts.SeverityName(ev.Severity),
                ["status"] = ev.Status == AlertEventStatus.Resolved ? "resolved" : "firing", ["title"] = ev.Title, ["message"] = ev.Message, ["text"] = text,
                ["value"] = ev.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                ["threshold"] = ev.Threshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                ["node.id"] = (node?.Id ?? ev.NodeId)?.ToString(), ["node.name"] = node?.PublicName ?? ev.NodeName, ["node.remark"] = node?.AdminRemark,
                ["node.countryCode"] = node?.CountryCodeOverride ?? node?.CountryCodeAuto ?? "",
                ["startedAt"] = ev.StartedAt.ToString("O"), ["resolvedAt"] = ev.ResolvedAt?.ToString("O"),
                ["site.title"] = s.SiteTitle, ["site.url"] = siteUrl,
            });
        }
        else
        {
            body = JsonSerializer.Serialize(new
            {
                @event = eventName,
                id = ev.Id,
                rule = AlertTexts.RuleSlug(ev.Rule),
                severity = AlertTexts.SeverityName(ev.Severity),
                status = ev.Status == AlertEventStatus.Resolved ? "resolved" : "firing",
                title = ev.Title,
                message = ev.Message,
                text,
                value = ev.Value,
                threshold = ev.Threshold,
                node = new { id = node?.Id ?? ev.NodeId, name = node?.PublicName ?? ev.NodeName, remark = node?.AdminRemark, countryCode = node?.CountryCodeOverride ?? node?.CountryCodeAuto ?? "" },
                startedAt = ev.StartedAt,
                resolvedAt = ev.ResolvedAt,
                site = new { title = s.SiteTitle, url = siteUrl },
            }, JsonOpts);
        }

        var http = httpFactory.CreateClient("notify");
        using var req = new HttpRequestMessage(string.Equals(cfg.Method, "PUT", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Put : HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("snm-master", typeof(NotificationDispatcher).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        req.Headers.TryAddWithoutValidation("X-SNM-Event", eventName);
        req.Headers.TryAddWithoutValidation("X-SNM-Delivery", Guid.NewGuid().ToString("N"));
        req.Headers.TryAddWithoutValidation("X-SNM-Timestamp", ts);
        if (!string.IsNullOrEmpty(cfg.Secret))
        {
            var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(cfg.Secret), Encoding.UTF8.GetBytes(ts + "." + body)));
            req.Headers.TryAddWithoutValidation("X-SNM-Signature", "sha256=" + sig);
        }
        if (cfg.Headers is not null)
        {
            foreach (var (k, v) in cfg.Headers.Take(10))
            {
                if (string.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSec, 3, 60)));
        using var resp = await http.SendAsync(req, cts.Token);
        var respBody = await resp.Content.ReadAsStringAsync(cts.Token);
        var ok = resp.IsSuccessStatusCode;
        return new DeliveryResult(ok, (int)resp.StatusCode, ok ? null : $"HTTP {(int)resp.StatusCode}: {Truncate(respBody)}", (int)sw.ElapsedMilliseconds, Truncate(respBody));
    }

    /// <summary>{{placeholder}} substitution; values are JSON-escaped without surrounding quotes.</summary>
    public static string RenderTemplate(string template, IReadOnlyDictionary<string, string?> values)
    {
        var sb = new StringBuilder(template.Length + 64);
        var i = 0;
        while (i < template.Length)
        {
            var start = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(template, i, template.Length - i); break; }
            var end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0) { sb.Append(template, i, template.Length - i); break; }
            sb.Append(template, i, start - i);
            var key = template[(start + 2)..end].Trim();
            if (values.TryGetValue(key, out var v))
            {
                var escaped = JsonSerializer.Serialize(v ?? "");
                sb.Append(escaped, 1, escaped.Length - 2);
            }
            i = end + 2;
        }
        return sb.ToString();
    }

    private static string? Truncate(string? s) => s is null ? null : s.Length <= 1000 ? s : s[..1000];

    /// <summary>Masks secrets for API output.</summary>
    public static string MaskConfig(string type, string configJson)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(configJson)?.AsObject();
            if (node is null) return "{}";
            if (type == ChannelTypes.Telegram && node["botToken"] is { } t)
            {
                var v = t.GetValue<string>();
                node["botToken"] = v.Length > 6 ? v[..4] + "****" : "****";
            }
            if (type == ChannelTypes.Webhook && node["secret"] is { } sec && !string.IsNullOrEmpty(sec.GetValue<string>())) node["secret"] = "****";
            return node.ToJsonString();
        }
        catch (JsonException)
        {
            return "{}";
        }
    }
}
