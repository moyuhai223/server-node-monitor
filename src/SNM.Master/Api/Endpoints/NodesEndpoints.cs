using System.Text.Json;
using SNM.Master.Api.Dto;
using SNM.Master.Services;

namespace SNM.Master.Api.Endpoints;

public sealed record ReorderRequest(int[]? Ids);

/// <summary>Node endpoints (docs/API.md 4).</summary>
public static class NodesEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/nodes").RequireAuthorization("Admin");

        g.MapGet("/", async (HttpRequest req, NodeService nodes, CancellationToken ct) =>
        {
            var (pageNo, pageSize) = req.Paging(defaultSize: 0);
            var q = new NodeListQuery(
                req.Query["keyword"],
                int.TryParse(req.Query["status"], out var st) ? st : null,
                bool.TryParse(req.Query["enabled"], out var en) ? en : null,
                pageNo, pageSize, req.Query["sortBy"], req.Query["sortDir"]);
            return Results.Ok(ApiResponse.Ok(await nodes.ListAsync(q, ct)));
        });

        g.MapPost("/", async (JsonElement body, HttpContext ctx, NodeService nodes, ILoggerFactory lf, CancellationToken ct) =>
        {
            var dto = await nodes.CreateAsync(body, ct);
            Audit(lf, ctx, $"create node {dto.Id}");
            return Results.Created($"/api/nodes/{dto.Id}", ApiResponse.Ok(dto));
        });

        g.MapPost("/reorder", async (ReorderRequest req, HttpContext ctx, NodeService nodes, ILoggerFactory lf, CancellationToken ct) =>
        {
            await nodes.ReorderAsync(req.Ids ?? [], ct);
            Audit(lf, ctx, "reorder nodes");
            return Results.Ok(ApiResponse.Ok(new { }));
        });

        g.MapGet("/{id:int}", async (int id, NodeService nodes, CancellationToken ct) => Results.Ok(ApiResponse.Ok(await nodes.GetAsync(id, ct))));

        g.MapPatch("/{id:int}", async (int id, JsonElement body, HttpContext ctx, NodeService nodes, ILoggerFactory lf, CancellationToken ct) =>
        {
            var dto = await nodes.UpdateAsync(id, body, ct);
            Audit(lf, ctx, $"update node {id}");
            return Results.Ok(ApiResponse.Ok(dto));
        });

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, NodeService nodes, ILoggerFactory lf, CancellationToken ct) =>
        {
            await nodes.DeleteAsync(id, ct);
            Audit(lf, ctx, $"delete node {id}", warn: true);
            return Results.Ok(ApiResponse.Ok(new { }));
        });

        g.MapPost("/{id:int}/rotate-key", async (int id, HttpContext ctx, NodeService nodes, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (key, at) = await nodes.RotateKeyAsync(id, ct);
            Audit(lf, ctx, $"rotate key of node {id}", warn: true);
            return Results.Ok(ApiResponse.Ok(new { agentKey = key, keyRotatedAt = at }));
        });

        g.MapPost("/{id:int}/reveal-key", (int id, HttpContext ctx, NodeService nodes) =>
        {
            var key = nodes.RevealKey(id, ctx.User.Identity?.Name, ctx.ClientIp());
            return Results.Ok(ApiResponse.Ok(new { agentKey = key }));
        });

        g.MapGet("/{id:int}/install-script", async (int id, string? os, bool? renew, HttpContext ctx, InstallScriptService installer, CancellationToken ct) =>
        {
            var info = await installer.IssueAsync(id, os, renew == true, ctx.Request, ct);
            return Results.Ok(ApiResponse.Ok(new
            {
                os = info.Os, token = info.Token, expiresAt = info.ExpiresAt, url = info.Url, oneLiner = info.OneLiner, uninstallOneLiner = info.UninstallOneLiner, script = info.Script,
            }));
        });

        g.MapGet("/{id:int}/metrics", async (int id, string? range, long? from, long? to, NodeService nodes, CancellationToken ct) =>
            Results.Ok(ApiResponse.Ok(await nodes.MetricsAsync(id, range, from, to, ct))));

        g.MapGet("/{id:int}/traffic", async (int id, int? periods, NodeService nodes, CancellationToken ct) =>
            Results.Ok(ApiResponse.Ok(await nodes.TrafficAsync(id, periods ?? 12, ct))));

        g.MapPost("/{id:int}/traffic/reset", async (int id, HttpContext ctx, NodeService nodes, ILoggerFactory lf, CancellationToken ct) =>
        {
            var traffic = await nodes.ResetTrafficAsync(id, ct);
            Audit(lf, ctx, $"reset traffic of node {id}", warn: true);
            return Results.Ok(ApiResponse.Ok(traffic.Current));
        });

        g.MapGet("/{id:int}/alerts", async (int id, HttpRequest req, AlertQueryService alerts, CancellationToken ct) =>
            Results.Ok(ApiResponse.Ok(await alerts.QueryAsync(AlertQueryService.Parse(req, forcedNodeId: id), ct))));
    }

    public static void Audit(ILoggerFactory lf, HttpContext ctx, string what, bool warn = false)
    {
        var logger = lf.CreateLogger("SNM.Api");
        var msg = "{Method} {Path} by {User} from {Ip}: {What}";
        if (warn) logger.LogWarning(msg, ctx.Request.Method, ctx.Request.Path, ctx.User.Identity?.Name, ctx.ClientIp(), what);
        else logger.LogInformation(msg, ctx.Request.Method, ctx.Request.Path, ctx.User.Identity?.Name, ctx.ClientIp(), what);
    }
}
