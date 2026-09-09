using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SNM.Master.Alerting;
using SNM.Master.Background;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Services;

namespace SNM.Master.Api.Endpoints;

/// <summary>Settings, notification channels and GeoIP endpoints (docs/API.md 6).</summary>
public static class SettingsEndpoints
{
    private static readonly Regex BotTokenRule = new(@"^\d+:[A-Za-z0-9_-]{30,}$", RegexOptions.Compiled);

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings").RequireAuthorization("Admin");

        g.MapGet("/", (SettingsService settings, GeoIpService geoIp) => Results.Ok(ApiResponse.Ok(Export(settings, geoIp))));

        g.MapPatch("/", async (JsonElement body, HttpContext ctx, SettingsService settings, GeoIpService geoIp, NodeService nodes, ILoggerFactory lf, CancellationToken ct) =>
        {
            var flat = SettingsService.Flatten(body);
            if (flat.Count == 0) throw ApiException.BadRequest("没有可更新的设置项");
            var before = settings.Snapshot.StatusIntervalSec;
            var errors = await settings.UpdateAsync(flat, ct);
            if (errors.Count > 0) throw ApiException.Validation(errors.GroupBy(e => e.Key).ToDictionary(g2 => g2.Key, g2 => g2.Select(e => e.Message).ToArray()));
            if (before != settings.Snapshot.StatusIntervalSec) await nodes.PushConfigureAllAsync(ct);
            NodesEndpoints.Audit(lf, ctx, $"update settings: {string.Join(", ", flat.Keys)}");
            return Results.Ok(ApiResponse.Ok(Export(settings, geoIp)));
        });

        // ---- channels
        g.MapGet("/channels", async (IDbContextFactory<SnmDbContext> dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
            return Results.Ok(ApiResponse.Ok(rows.Select(ToDto).ToArray()));
        });

        g.MapPost("/channels", async (JsonElement body, HttpContext ctx, IDbContextFactory<SnmDbContext> dbFactory, ILoggerFactory lf, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var ch = new NotificationChannel { CreatedAt = now, UpdatedAt = now };
            Apply(ch, body, isCreate: true);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.NotificationChannels.Add(ch);
            await db.SaveChangesAsync(ct);
            NodesEndpoints.Audit(lf, ctx, $"create channel {ch.Id} ({ch.Type})");
            return Results.Created($"/api/settings/channels/{ch.Id}", ApiResponse.Ok(ToDto(ch)));
        });

        g.MapPatch("/channels/{id:int}", async (int id, JsonElement body, HttpContext ctx, IDbContextFactory<SnmDbContext> dbFactory, ILoggerFactory lf, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ch = await db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("渠道不存在");
            Apply(ch, body, isCreate: false);
            ch.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            NodesEndpoints.Audit(lf, ctx, $"update channel {id}");
            return Results.Ok(ApiResponse.Ok(ToDto(ch)));
        });

        g.MapDelete("/channels/{id:int}", async (int id, HttpContext ctx, IDbContextFactory<SnmDbContext> dbFactory, ILoggerFactory lf, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var n = await db.NotificationChannels.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
            if (n == 0) throw ApiException.NotFound("渠道不存在");
            NodesEndpoints.Audit(lf, ctx, $"delete channel {id}", warn: true);
            return Results.Ok(ApiResponse.Ok(new { }));
        });

        g.MapPost("/channels/{id:int}/test", async (int id, IDbContextFactory<SnmDbContext> dbFactory, NotificationDispatcher dispatcher, CancellationToken ct) =>
        {
            NotificationChannel ch;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                ch = await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("渠道不存在");
            }
            return TestResult(await dispatcher.TestAsync(ch, ct));
        });

        g.MapPost("/channels/test", async (JsonElement body, NotificationDispatcher dispatcher, CancellationToken ct) =>
        {
            var ch = new NotificationChannel();
            Apply(ch, body, isCreate: true);
            return TestResult(await dispatcher.TestAsync(ch, ct));
        });

        // ---- geoip
        g.MapPost("/geoip/refresh", async (GeoIpService geoIp, SettingsService settings, CancellationToken ct) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            try { await geoIp.RefreshAsync(cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ApiException.Upstream(ApiCodes.GeoIpRefreshFailed, "GeoIP 刷新失败", new { detail = ex.Message });
            }
            return Results.Ok(ApiResponse.Ok(GeoStatus(geoIp, settings)));
        });

        g.MapGet("/geoip/status", (HttpContext ctx, GeoIpService geoIp, SettingsService settings) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            var status = GeoStatus(geoIp, settings);
            return Results.Ok(ApiResponse.Ok(new
            {
                status.enabled, status.ready, status.lastRefreshUtc, status.ipv4Rows, status.ipv6Rows, status.lastError,
                lookup = new { ip = ip?.ToString(), cc = geoIp.Lookup(ip) },
            }));
        });
    }

    private static IResult TestResult(DeliveryResult r)
    {
        if (r.Ok) return Results.Ok(ApiResponse.Ok(new { ok = true, statusCode = r.StatusCode, elapsedMs = r.ElapsedMs, response = r.Response }));
        throw ApiException.Upstream(ApiCodes.ChannelTestFailed, "通知渠道测试失败", new { ok = false, statusCode = r.StatusCode, elapsedMs = r.ElapsedMs, error = r.Error, response = r.Response });
    }

    private static (bool enabled, bool ready, DateTime? lastRefreshUtc, int ipv4Rows, int ipv6Rows, string? lastError) GeoStatus(GeoIpService geoIp, SettingsService settings) =>
        (settings.Snapshot.GeoIpEnabled, geoIp.Ready, geoIp.LastRefreshUtc, geoIp.Ipv4Rows, geoIp.Ipv6Rows, geoIp.LastError);

    private static JsonObject Export(SettingsService settings, GeoIpService geoIp)
    {
        var root = settings.Export();
        var geo = root["geoip"] as JsonObject ?? new JsonObject();
        geo["ready"] = geoIp.Ready;
        geo["lastRefreshUtc"] = geoIp.LastRefreshUtc?.ToString("O");
        geo["ipv4Rows"] = geoIp.Ipv4Rows;
        geo["ipv6Rows"] = geoIp.Ipv6Rows;
        geo["lastError"] = geoIp.LastError;
        root["geoip"] = geo;
        var ret = root["retention"] as JsonObject ?? new JsonObject();
        ret["metrics1mHours"] = RetentionService.Metrics1mHours;
        ret["metrics1hDays"] = RetentionService.Metrics1hDays;
        ret["metrics1dDays"] = RetentionService.Metrics1dDays;
        root["retention"] = ret;
        if (root["alert"] is JsonObject alert) alert.Remove("lastExpiryCheckDate");
        return root;
    }

    // ---- channel model

    private static object ToDto(NotificationChannel c) => new
    {
        id = c.Id, type = c.Type, name = c.Name, enabled = c.Enabled, ruleMask = c.RuleMask, minSeverity = c.MinSeverity,
        config = JsonNode.Parse(NotificationDispatcher.MaskConfig(c.Type, c.ConfigJson)),
        lastTestAt = c.LastTestAt, lastSuccessAt = c.LastSuccessAt, lastError = c.LastError, createdAt = c.CreatedAt, updatedAt = c.UpdatedAt,
    };

    private static void Apply(NotificationChannel ch, JsonElement body, bool isCreate)
    {
        var v = new Validator();
        if (body.ValueKind != JsonValueKind.Object) { v.Add("body", "请求体必须是 JSON 对象"); v.ThrowIfInvalid(); }

        if (body.TryGetProperty("type", out var t))
        {
            var type = t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim().ToLowerInvariant() : "";
            if (type is not (ChannelTypes.Telegram or ChannelTypes.Webhook)) v.Add("type", "须为 telegram 或 webhook");
            else if (!isCreate && ch.Type.Length > 0 && ch.Type != type) v.Add("type", "渠道类型不可修改");
            else ch.Type = type;
        }
        else if (isCreate) v.Add("type", "不能为空");

        if (body.TryGetProperty("name", out var n))
        {
            var name = n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            if (name.Length is 0 or > 64) v.Add("name", "长度必须在 1–64 之间"); else ch.Name = name;
        }
        else if (isCreate) v.Add("name", "不能为空");

        if (body.TryGetProperty("enabled", out var e))
        {
            if (e.ValueKind is JsonValueKind.True or JsonValueKind.False) ch.Enabled = e.GetBoolean(); else v.Add("enabled", "必须是布尔值");
        }
        if (body.TryGetProperty("ruleMask", out var rm))
        {
            if (rm.ValueKind == JsonValueKind.Number && rm.TryGetInt32(out var mask) && mask is >= 0 and <= 126) ch.RuleMask = mask; else v.Add("ruleMask", "须在 0–126 之间");
        }
        if (body.TryGetProperty("minSeverity", out var ms))
        {
            if (ms.ValueKind == JsonValueKind.Number && ms.TryGetInt32(out var sev) && sev is >= 1 and <= 3) ch.MinSeverity = sev; else v.Add("minSeverity", "须在 1–3 之间");
        }

        if (body.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
        {
            var existing = JsonNode.Parse(string.IsNullOrWhiteSpace(ch.ConfigJson) ? "{}" : ch.ConfigJson) as JsonObject ?? new JsonObject();
            if (ch.Type == ChannelTypes.Telegram) ApplyTelegram(existing, cfg, v);
            else if (ch.Type == ChannelTypes.Webhook) ApplyWebhook(existing, cfg, v);
            ch.ConfigJson = existing.ToJsonString();
        }
        else if (isCreate) v.Add("config", "不能为空");

        v.ThrowIfInvalid();
        if (isCreate && (ch.ConfigJson == "{}" || string.IsNullOrEmpty(ch.ConfigJson)))
        {
            v.Add("config", "不能为空");
            v.ThrowIfInvalid();
        }
        // final shape validation
        var final = JsonNode.Parse(ch.ConfigJson) as JsonObject ?? new JsonObject();
        if (ch.Type == ChannelTypes.Telegram)
        {
            v.When(!BotTokenRule.IsMatch(final["botToken"]?.GetValue<string>() ?? ""), "config.botToken", "Bot Token 格式不正确");
            v.When(string.IsNullOrWhiteSpace(final["chatId"]?.GetValue<string>()), "config.chatId", "不能为空");
        }
        else if (ch.Type == ChannelTypes.Webhook)
        {
            var url = final["url"]?.GetValue<string>() ?? "";
            v.When(!(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"), "config.url", "须为 http(s) 地址");
        }
        v.ThrowIfInvalid();
    }

    private static void ApplyTelegram(JsonObject cfg, JsonElement input, Validator v)
    {
        if (input.TryGetProperty("botToken", out var tok) && tok.ValueKind == JsonValueKind.String && tok.GetString() is { } s && s != "****" && !s.EndsWith("****", StringComparison.Ordinal))
            cfg["botToken"] = s.Trim();
        if (input.TryGetProperty("chatId", out var chat))
        {
            cfg["chatId"] = chat.ValueKind switch { JsonValueKind.String => chat.GetString()!.Trim(), JsonValueKind.Number => chat.GetRawText(), _ => "" };
        }
        if (input.TryGetProperty("parseMode", out var pm) && pm.ValueKind == JsonValueKind.String)
        {
            var mode = pm.GetString()!;
            if (mode is "HTML" or "MarkdownV2" or "Markdown" or "none") cfg["parseMode"] = mode; else v.Add("config.parseMode", "须为 HTML / MarkdownV2 / none");
        }
        if (input.TryGetProperty("disableNotification", out var dn) && dn.ValueKind is JsonValueKind.True or JsonValueKind.False) cfg["disableNotification"] = dn.GetBoolean();
        if (input.TryGetProperty("messageThreadId", out var th))
        {
            if (th.ValueKind == JsonValueKind.Null) cfg.Remove("messageThreadId");
            else if (th.ValueKind == JsonValueKind.Number && th.TryGetInt64(out var id)) cfg["messageThreadId"] = id;
            else v.Add("config.messageThreadId", "必须是整数");
        }
        cfg["parseMode"] ??= "HTML";
    }

    private static void ApplyWebhook(JsonObject cfg, JsonElement input, Validator v)
    {
        if (input.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String) cfg["url"] = url.GetString()!.Trim();
        if (input.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
        {
            var method = m.GetString()!.ToUpperInvariant();
            if (method is "POST" or "PUT") cfg["method"] = method; else v.Add("config.method", "须为 POST 或 PUT");
        }
        if (input.TryGetProperty("secret", out var sec))
        {
            if (sec.ValueKind == JsonValueKind.Null) cfg.Remove("secret");
            else if (sec.ValueKind == JsonValueKind.String && sec.GetString() is { } s && s != "****") cfg["secret"] = s;
        }
        if (input.TryGetProperty("headers", out var h))
        {
            if (h.ValueKind == JsonValueKind.Null) cfg.Remove("headers");
            else if (h.ValueKind == JsonValueKind.Object)
            {
                var headers = new JsonObject();
                var count = 0;
                foreach (var p in h.EnumerateObject())
                {
                    if (++count > 10) { v.Add("config.headers", "最多 10 项"); break; }
                    if (p.Value.ValueKind != JsonValueKind.String || p.Name.Length == 0 || p.Name.Length > 64 || string.Equals(p.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    { v.Add("config.headers", $"非法的请求头 {p.Name}"); continue; }
                    headers[p.Name] = p.Value.GetString();
                }
                cfg["headers"] = headers;
            }
            else v.Add("config.headers", "必须是对象");
        }
        if (input.TryGetProperty("bodyTemplate", out var bt))
        {
            if (bt.ValueKind == JsonValueKind.Null) cfg.Remove("bodyTemplate");
            else if (bt.ValueKind == JsonValueKind.String) { if (bt.GetString()!.Length > 8000) v.Add("config.bodyTemplate", "长度不能超过 8000"); else cfg["bodyTemplate"] = bt.GetString(); }
        }
        if (input.TryGetProperty("timeoutSec", out var to))
        {
            if (to.ValueKind == JsonValueKind.Number && to.TryGetInt32(out var sec2) && sec2 is >= 3 and <= 60) cfg["timeoutSec"] = sec2; else v.Add("config.timeoutSec", "须在 3–60 之间");
        }
        cfg["method"] ??= "POST";
        cfg["timeoutSec"] ??= 10;
    }
}
