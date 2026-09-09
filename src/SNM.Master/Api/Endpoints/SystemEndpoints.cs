using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using SNM.Contracts;
using SNM.Master.Alerting;
using SNM.Master.Background;
using SNM.Master.Data;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Api.Endpoints;

/// <summary>Dashboard, system info, health checks and the public install-script endpoint (docs/API.md 3, 4.11, 6.5, 6.6).</summary>
public static class SystemEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api").RequireAuthorization("Admin");

        auth.MapGet("/dashboard/summary", async (DashboardService dashboard, CancellationToken ct) => Results.Ok(ApiResponse.Ok(await dashboard.SummaryAsync(ct))));

        auth.MapGet("/system/info", (NodeRegistry registry, AgentConnectionTracker tracker, HubStats hubs, NotificationDispatcher dispatcher, DataPaths paths) =>
            Results.Ok(ApiResponse.Ok(new
            {
                version = AppInfo.ShortVersion,
                protocolVersion = ProtocolConstants.ProtocolVersion,
                framework = RuntimeInformation.FrameworkDescription,
                os = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}",
                startedAt = AppInfo.StartedAt,
                uptimeSec = AppInfo.UptimeSec,
                dataDir = paths.DataDir,
                dbSizeBytes = paths.DbSizeBytes(),
                walSizeBytes = paths.WalSizeBytes(),
                nodes = new { total = registry.Count, connected = registry.All.Count(n => n.Connected) },
                hubs = new { publicClients = hubs.PublicClients, adminClients = hubs.AdminClients, agentConnections = tracker.Count },
                queues = new { flushPending = registry.All.Sum(n => n.FlushQueue.Count), notificationsPending = dispatcher.Pending },
            })));

        app.MapGet("/healthz", () => Results.Text("ok"));

        app.MapGet("/api/health", async (IDbContextFactory<SnmDbContext> dbFactory, GeoIpService geoIp, CancellationToken ct) =>
        {
            var dbOk = false;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                dbOk = await db.Database.CanConnectAsync(ct);
            }
            catch { dbOk = false; }
            var body = ApiResponse.Ok(new { status = dbOk ? "ok" : "degraded", db = dbOk, geoip = geoIp.Ready });
            return dbOk ? Results.Ok(body) : Results.Json(body, statusCode: 503);
        });

        app.MapGet("/install/{token}", async (string token, string? os, HttpContext ctx, InstallScriptService installer, CancellationToken ct) =>
        {
            var script = await installer.RenderByTokenAsync(token, os, ctx.ClientIp(), ctx.Request, ct);
            ctx.Response.Headers.CacheControl = "no-store";
            if (script is null) return Results.Text("install token invalid or expired", "text/plain", statusCode: 404);
            var windows = InstallScriptService.NormalizeOs(os) == "windows";
            return Results.Text(script, windows ? "text/plain; charset=utf-8" : "text/x-shellscript; charset=utf-8");
        }).RequireRateLimiting("install");
    }
}

/// <summary>Live browser connection counters for /api/system/info.</summary>
public sealed class HubStats
{
    private int _public, _admin;
    public int PublicClients => Volatile.Read(ref _public);
    public int AdminClients => Volatile.Read(ref _admin);
    public void PublicConnected() => Interlocked.Increment(ref _public);
    public void PublicDisconnected() => Interlocked.Decrement(ref _public);
    public void AdminConnected() => Interlocked.Increment(ref _admin);
    public void AdminDisconnected() => Interlocked.Decrement(ref _admin);
}
