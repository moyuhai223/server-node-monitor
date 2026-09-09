using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SNM.Master.Alerting;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Services;

namespace SNM.Master.Api.Endpoints;

public sealed record AlertQuery(int? NodeId, int? Rule, int? Status, int? Severity, DateTime? From, DateTime? To, bool? Acknowledged, int PageNo, int PageSize, string? SortBy, string? SortDir);

/// <summary>Alert event queries shared by /api/alerts and /api/nodes/{id}/alerts (docs/API.md 5).</summary>
public sealed class AlertQueryService(IDbContextFactory<SnmDbContext> dbFactory)
{
    public static AlertQuery Parse(HttpRequest req, int? forcedNodeId = null)
    {
        var (pageNo, pageSize) = req.Paging();
        return new AlertQuery(
            forcedNodeId ?? (int.TryParse(req.Query["nodeId"], out var n) ? n : null),
            int.TryParse(req.Query["rule"], out var r) ? r : null,
            int.TryParse(req.Query["status"], out var s) ? s : null,
            int.TryParse(req.Query["severity"], out var sev) ? sev : null,
            ParseTime(req.Query["from"]), ParseTime(req.Query["to"]),
            bool.TryParse(req.Query["acknowledged"], out var ack) ? ack : null,
            pageNo, pageSize, req.Query["sortBy"], req.Query["sortDir"]);
    }

    private static DateTime? ParseTime(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public async Task<PagedData<object>> QueryAsync(AlertQuery q, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.AlertEvents.AsNoTracking().AsQueryable();
        if (q.NodeId is { } nodeId) query = query.Where(x => x.NodeId == nodeId);
        if (q.Rule is { } rule) query = query.Where(x => x.Rule == rule);
        if (q.Status is { } status) query = query.Where(x => x.Status == status);
        if (q.Severity is { } severity) query = query.Where(x => x.Severity == severity);
        if (q.From is { } from) query = query.Where(x => x.StartedAt >= from);
        if (q.To is { } to) query = query.Where(x => x.StartedAt <= to);
        if (q.Acknowledged is { } ack) query = ack ? query.Where(x => x.AcknowledgedAt != null) : query.Where(x => x.AcknowledgedAt == null);

        var desc = !string.Equals(q.SortDir, "asc", StringComparison.OrdinalIgnoreCase);
        query = (q.SortBy ?? "startedAt") switch
        {
            "resolvedAt" => desc ? query.OrderByDescending(x => x.ResolvedAt).ThenByDescending(x => x.Id) : query.OrderBy(x => x.ResolvedAt).ThenBy(x => x.Id),
            "severity" => desc ? query.OrderByDescending(x => x.Severity).ThenByDescending(x => x.StartedAt) : query.OrderBy(x => x.Severity).ThenBy(x => x.StartedAt),
            _ => desc ? query.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id) : query.OrderBy(x => x.StartedAt).ThenBy(x => x.Id),
        };

        var total = await query.CountAsync(ct);
        if (q.PageSize > 0) query = query.Skip((q.PageNo - 1) * q.PageSize).Take(q.PageSize);
        var rows = await query.ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToArray();
        var deliveries = ids.Length == 0 ? [] : await db.NotificationDeliveries.AsNoTracking().Where(d => d.EventId != null && ids.Contains(d.EventId.Value)).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var byEvent = deliveries.GroupBy(d => d.EventId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        return new PagedData<object>
        {
            PageData = rows.Select(r => ToDto(r, byEvent.GetValueOrDefault(r.Id))).ToList(),
            Total = total, PageNo = q.PageNo, PageSize = q.PageSize,
        };
    }

    public async Task<object?> GetAsync(long id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ev = await db.AlertEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (ev is null) return null;
        var deliveries = await db.NotificationDeliveries.AsNoTracking().Where(d => d.EventId == id).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        return ToDto(ev, deliveries);
    }

    public async Task<object[]> ActiveAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AlertEvents.AsNoTracking().Where(x => x.Status == 1).OrderByDescending(x => x.Severity).ThenByDescending(x => x.StartedAt).ToListAsync(ct);
        return rows.Select(r => ToDto(r, null)).ToArray();
    }

    public async Task<DateTime> AckAsync(long id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ev = await db.AlertEvents.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("告警不存在");
        ev.AcknowledgedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ev.AcknowledgedAt.Value;
    }

    public async Task<int> DeleteAsync(DateTime? before, int? status, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.AlertEvents.AsQueryable();
        if (before is { } b) query = query.Where(x => x.StartedAt < b);
        if (status is { } s) query = query.Where(x => x.Status == s);
        else query = query.Where(x => x.Status == 2);   // never delete firing events by default
        return await query.ExecuteDeleteAsync(ct);
    }

    public static object ToDto(AlertEvent e, List<NotificationDelivery>? deliveries) => new
    {
        id = e.Id, nodeId = e.NodeId, nodeName = e.NodeName, rule = e.Rule, ruleName = AlertTexts.RuleName(e.Rule), subject = e.Subject, status = e.Status, severity = e.Severity,
        title = e.Title, message = e.Message, value = e.Value, threshold = e.Threshold, startedAt = e.StartedAt, resolvedAt = e.ResolvedAt, notified = e.Notified, acknowledgedAt = e.AcknowledgedAt,
        deliveries = (deliveries ?? []).Select(d => new
        {
            channelId = d.ChannelId, channelName = d.ChannelName, kind = d.Kind, attempt = d.Attempt, ok = d.Ok, statusCode = d.StatusCode, error = d.Error, elapsedMs = d.ElapsedMs, createdAt = d.CreatedAt,
        }).ToArray(),
    };
}

public static class AlertsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/alerts").RequireAuthorization("Admin");

        g.MapGet("/", async (HttpRequest req, AlertQueryService alerts, CancellationToken ct) =>
            Results.Ok(ApiResponse.Ok(await alerts.QueryAsync(AlertQueryService.Parse(req), ct))));

        g.MapGet("/active", async (AlertQueryService alerts, CancellationToken ct) => Results.Ok(ApiResponse.Ok(await alerts.ActiveAsync(ct))));

        g.MapGet("/{id:long}", async (long id, AlertQueryService alerts, CancellationToken ct) =>
        {
            var dto = await alerts.GetAsync(id, ct) ?? throw ApiException.NotFound("告警不存在");
            return Results.Ok(ApiResponse.Ok(dto));
        });

        g.MapPost("/{id:long}/ack", async (long id, AlertQueryService alerts, CancellationToken ct) =>
            Results.Ok(ApiResponse.Ok(new { acknowledgedAt = await alerts.AckAsync(id, ct) })));

        g.MapDelete("/", async (HttpRequest req, HttpContext ctx, AlertQueryService alerts, ILoggerFactory lf, CancellationToken ct) =>
        {
            DateTime? before = DateTime.TryParse(req.Query["before"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var b) ? b : null;
            int? status = int.TryParse(req.Query["status"], out var s) ? s : null;
            var deleted = await alerts.DeleteAsync(before, status, ct);
            NodesEndpoints.Audit(lf, ctx, $"delete {deleted} alert events", warn: true);
            return Results.Ok(ApiResponse.Ok(new { deleted }));
        });
    }
}
