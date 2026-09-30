using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Api.Endpoints;

public static class ProbesEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/probes").RequireAuthorization("Admin");
        group.MapGet("/", (ProbeService probes, NodeRegistry registry) => Results.Ok(ApiResponse.Ok(new
        {
            targets = probes.Targets,
            nodes = registry.All.OrderBy(n => n.Meta.SortOrder).Select(n => new { id = n.Id, name = n.Meta.PublicName, enabled = n.Meta.Enabled, probes = probes.Snapshot(n) }).ToArray(),
        })));
        group.MapPut("/", async (ProbeTarget[] targets, ProbeService probes, HttpContext ctx, ILoggerFactory lf, CancellationToken ct) =>
        {
            await probes.SaveAsync(targets, ct);
            NodesEndpoints.Audit(lf, ctx, $"update {targets.Length} probe targets");
            return Results.Ok(ApiResponse.Ok(new { targets = probes.Targets }));
        });
    }
}
