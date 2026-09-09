using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Master.Auth;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Hubs;

/// <summary>/hubs/agent - MessagePack, AgentKey auth. Only three uplink methods; the only downlink is "configure".</summary>
[Authorize(AuthenticationSchemes = AgentKeyAuthenticationHandler.SchemeName, Roles = "agent")]
public sealed class AgentHub(NodeRegistry registry, NodeIngestService ingest, AgentConnectionTracker tracker, ILogger<AgentHub> logger) : Hub
{
    private const string NodeIdKey = "nodeId";
    private const string RemoteIpKey = "remoteIp";
    private const string DropWarnedKey = "dropWarned";

    private NodeRuntime? CurrentNode()
    {
        if (Context.Items.TryGetValue(NodeIdKey, out var cached) && cached is int id) return registry.Get(id);
        var claim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(claim, out id)) return null;
        Context.Items[NodeIdKey] = id;
        return registry.Get(id);
    }

    public override Task OnConnectedAsync()
    {
        var node = CurrentNode();
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        Context.Items[RemoteIpKey] = ip;
        if (node is null)
        {
            Context.Abort();
            return Task.CompletedTask;
        }
        tracker.Add(Context);
        logger.LogInformation("Agent connection {ConnectionId} for node {NodeId} from {Ip}", Context.ConnectionId, node.Id, ip);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.Remove(Context.ConnectionId);
        var node = CurrentNode();
        if (node is not null) ingest.OnDisconnected(node, Context.ConnectionId, exception);
        return Task.CompletedTask;
    }

    [HubMethodName(AgentHubMethods.Register)]
    public async Task<AgentConfigDto> Register(RegisterDto dto)
    {
        var now = DateTime.UtcNow;
        var node = CurrentNode() ?? throw new HubException("node not found");
        var ip = Context.Items.TryGetValue(RemoteIpKey, out var o) ? o as IPAddress : null;
        return await ingest.RegisterAsync(node, dto, Context, ip, now, Context.ConnectionAborted);
    }

    [HubMethodName(AgentHubMethods.Heartbeat)]
    public Task Heartbeat(HeartbeatDto dto)
    {
        var now = DateTime.UtcNow;
        var node = CurrentNode();
        if (node is null) { Context.Abort(); return Task.CompletedTask; }
        var drop = ingest.OnHeartbeat(node, dto, Context.ConnectionId, now);
        HandleDrop(node, drop);
        return Task.CompletedTask;
    }

    [HubMethodName(AgentHubMethods.ReportStatus)]
    public async Task ReportStatus(StatusReportDto dto)
    {
        var now = DateTime.UtcNow;
        var node = CurrentNode();
        if (node is null) { Context.Abort(); return; }
        var drop = await ingest.StatusAsync(node, dto, Context.ConnectionId, now, Context.ConnectionAborted);
        HandleDrop(node, drop);
    }

    private void HandleDrop(NodeRuntime node, DropReason drop)
    {
        switch (drop)
        {
            case DropReason.None:
                return;
            case DropReason.StaleConnection:
                logger.LogWarning("Node {NodeId}: message on stale connection {ConnectionId}; aborting", node.Id, Context.ConnectionId);
                Context.Abort();
                return;
            case DropReason.Unregistered:
                if (!Context.Items.ContainsKey(DropWarnedKey))
                {
                    Context.Items[DropWarnedKey] = true;
                    logger.LogWarning("Node {NodeId}: message before register on {ConnectionId}; dropped", node.Id, Context.ConnectionId);
                }
                return;
            case DropReason.Sequence:
                logger.LogDebug("Node {NodeId}: duplicate/out-of-order heartbeat dropped", node.Id);
                return;
        }
    }
}

/// <summary>/hubs/public - anonymous, sanitized data only (PRD 6.2).</summary>
public sealed class PublicHub(LiveSnapshotBuilder builder, SNM.Master.Api.Endpoints.HubStats stats) : Hub
{
    public override async Task OnConnectedAsync()
    {
        stats.PublicConnected();
        await Clients.Caller.SendAsync(PublicHubMethods.Snapshot, builder.PublicSnapshot(DateTime.UtcNow), Context.ConnectionAborted);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        stats.PublicDisconnected();
        return Task.CompletedTask;
    }

    [HubMethodName(PublicHubMethods.GetSnapshot)]
    public PublicSnapshotDto GetSnapshot() => builder.PublicSnapshot(DateTime.UtcNow);
}

/// <summary>/hubs/admin - JWT protected, full live data.</summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "admin")]
public sealed class AdminHub(LiveSnapshotBuilder builder, SNM.Master.Api.Endpoints.HubStats stats) : Hub
{
    public override async Task OnConnectedAsync()
    {
        stats.AdminConnected();
        await Clients.Caller.SendAsync(AdminHubMethods.Snapshot, builder.AdminSnapshot(DateTime.UtcNow), Context.ConnectionAborted);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        stats.AdminDisconnected();
        return Task.CompletedTask;
    }

    [HubMethodName(AdminHubMethods.GetSnapshot)]
    public AdminSnapshotDto GetSnapshot() => builder.AdminSnapshot(DateTime.UtcNow);

    [HubMethodName(AdminHubMethods.GetHistory)]
    public AdminHistoryDto? GetHistory(int nodeId) => builder.AdminHistory(nodeId);
}
