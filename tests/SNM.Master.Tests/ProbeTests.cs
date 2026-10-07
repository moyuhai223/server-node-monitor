using System.Net;
using System.Net.Http.Json;
using System.Text;
using MessagePack;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Contracts.Protocol;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Tests;

public class ProbeTests(MasterFactory factory) : IClassFixture<MasterFactory>
{
    [Fact]
    public async Task All_nodes_mode_includes_future_nodes_and_preserves_existing_windows()
    {
        using var admin = await factory.LoginAsync();
        var target = new ProbeTarget { Id = 10, Name = "全部节点线路", Address = "example.com", AllNodes = true };
        // No explicit node IDs are needed, even when configuring targets before nodes exist.
        (await admin.PutAsJsonAsync("/api/settings/probes", new[] { target })).EnsureSuccessStatusCode();
        var probes = factory.Services.GetRequiredService<ProbeService>();
        var registry = factory.Services.GetRequiredService<NodeRegistry>();
        var revision = Assert.Single(probes.Targets).Revision;
        var created = await MasterFactory.DataAsync(await admin.PostAsJsonAsync("/api/nodes", new { publicName = "Later node" }));
        var node = registry.Get(created.GetProperty("id").GetInt32())!;
        Assert.Equal(revision, Assert.Single(probes.Configuration(node).Targets).Revision);
        Assert.Single(probes.Snapshot(node));
        node.Registered = true;
        node.Connected = true;
        node.ConnectionId = "all-nodes-test";
        var result = new ProbeResultDto { Id = target.Id, Revision = revision, Microseconds = 1000 };
        Assert.True(probes.Accept(node, node.ConnectionId, result, DateTime.UtcNow));

        var later = await MasterFactory.DataAsync(await admin.PostAsJsonAsync("/api/nodes", new { publicName = "Another later node" }));
        Assert.Single(probes.Configuration(registry.Get(later.GetProperty("id").GetInt32())!).Targets);
        // Saving the same all-node definition does not depend on the current node inventory.
        (await admin.PutAsJsonAsync("/api/settings/probes", new[] { target with { NodeIds = [int.MaxValue] } })).EnsureSuccessStatusCode();
        Assert.Equal(revision, Assert.Single(probes.Targets).Revision);
        Assert.Empty(Assert.Single(probes.Targets).NodeIds);
        Assert.Single(Assert.Single(probes.Snapshot(node)).Points);
        var reloaded = new ProbeService(factory.Services.GetRequiredService<SettingsService>(), registry);
        Assert.True(Assert.Single(reloaded.Targets).AllNodes);
        Assert.Single(reloaded.Configuration(node).Targets);

        node.Meta.Enabled = false;
        Assert.Empty(probes.Configuration(node).Targets);
        Assert.Empty(probes.Snapshot(node));
        Assert.False(probes.Accept(node, node.ConnectionId, result, DateTime.UtcNow.AddMinutes(1)));
        node.Meta.Enabled = true;
        (await admin.PutAsJsonAsync("/api/settings/probes", new[] { target with { AllNodes = false, NodeIds = [node.Id] } })).EnsureSuccessStatusCode();
        Assert.NotEqual(revision, Assert.Single(probes.Targets).Revision);
        Assert.Empty(node.ProbeSamples);
        Assert.Empty(probes.Configuration(registry.Get(later.GetProperty("id").GetInt32())!).Targets);
        Assert.False(probes.Accept(node, node.ConnectionId, result, DateTime.UtcNow.AddMinutes(1)));
        (await admin.PutAsJsonAsync("/api/settings/probes", new[] { target with { Enabled = false } })).EnsureSuccessStatusCode();
        Assert.Empty(probes.Configuration(node).Targets);
        Assert.Empty(probes.Snapshot(node));
    }

    [Fact]
    public async Task Real_agent_hub_round_trip_privacy_revision_and_bounded_history()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/settings/probes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/settings/probes", Array.Empty<ProbeTarget>())).StatusCode);
        using var admin = await factory.LoginAsync();
        var created = await MasterFactory.DataAsync(await admin.PostAsJsonAsync("/api/nodes", new { publicName = "Probe Node", publicVisible = true }));
        var id = created.GetProperty("id").GetInt32();
        var target = new ProbeTarget { Id = 1, Name = "测试线路", Address = "private-target.example", Kind = 1, NodeIds = [id], IntervalSec = 10 };
        (await admin.PutAsJsonAsync("/api/settings/probes", new[] { target })).EnsureSuccessStatusCode();
        var builder = new HubConnectionBuilder().WithUrl(factory.Server.BaseAddress + "hubs/agent", HttpTransportType.LongPolling, o =>
        {
            o.Headers[ProtocolConstants.AgentKeyHeader] = created.GetProperty("agentKey").GetString()!;
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        });
        builder.Services.Replace(ServiceDescriptor.Singleton<IHubProtocol, SnmMessagePackHubProtocol>());
        await using var agent = builder.Build(); await agent.StartAsync();
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => agent.InvokeAsync<ProbeConfigDto>(AgentHubMethods.GetProbes));
        await agent.InvokeAsync<AgentConfigDto>(AgentHubMethods.Register, new RegisterDto { AgentVersion = "probe-test" });
        var config = await agent.InvokeAsync<ProbeConfigDto>(AgentHubMethods.GetProbes);
        var assigned = Assert.Single(config.Targets); Assert.Equal(target.Address, assigned.Address);
        await agent.InvokeAsync(AgentHubMethods.ProbeResult, new ProbeResultDto { Id = 1, Revision = assigned.Revision, Microseconds = 12340 });
        var registry = factory.Services.GetRequiredService<NodeRegistry>(); var node = registry.Get(id)!;
        var probes = factory.Services.GetRequiredService<ProbeService>();
        Assert.Equal(12340, Assert.Single(Assert.Single(probes.Snapshot(node)).Points).DurationUs);
        var publicBuilder = factory.Services.GetRequiredService<LiveSnapshotBuilder>();
        var bytes = MessagePackSerializer.Serialize(publicBuilder.PublicSnapshot(DateTime.UtcNow));
        Assert.DoesNotContain(target.Address, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(assigned.Revision, Encoding.UTF8.GetString(bytes));
        // Independent public connection gets the cached measurements as well.
        var publicConn = new HubConnectionBuilder().WithUrl(factory.Server.BaseAddress + "hubs/public", HttpTransportType.LongPolling,
            o => o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler()).AddMessagePackProtocol().Build();
        await using (publicConn)
        {
            await publicConn.StartAsync();
            var snapshot = await publicConn.InvokeAsync<PublicSnapshotDto>(PublicHubMethods.GetSnapshot);
            Assert.Single(snapshot.Nodes.Single(n => n.Id == id).Probes[0].Points);
        }
        var result = new ProbeResultDto { Id = 1, Revision = assigned.Revision, Status = 1, Microseconds = 99 };
        var now = DateTime.UtcNow;
        Assert.False(probes.Accept(node, "wrong-connection", result, now.AddSeconds(10)));
        Assert.False(probes.Accept(node, node.ConnectionId!, result, now)); // too frequent
        for (var i = 1; i <= 70; i++) Assert.True(probes.Accept(node, node.ConnectionId!, result, now.AddSeconds(10 * i)));
        var points = Assert.Single(probes.Snapshot(node)).Points;
        Assert.Equal(60, points.Length); Assert.All(points, p => Assert.Equal(-1, p.DurationUs));
        (await admin.PutAsJsonAsync("/api/settings/probes", new[] { target with { Address = "changed.example" } })).EnsureSuccessStatusCode();
        Assert.Empty(Assert.Single(probes.Snapshot(node)).Points);
        Assert.False(probes.Accept(node, node.ConnectionId!, result, now.AddHours(1)));
        var other = new NodeRuntime(new SNM.Master.Data.Entities.Node { Id = id + 100 });
        Assert.Empty(probes.Configuration(other).Targets);
        (await admin.PutAsJsonAsync("/api/settings/probes", Array.Empty<ProbeTarget>())).EnsureSuccessStatusCode();
        Assert.Empty(probes.Configuration(node).Targets); Assert.Empty(probes.Snapshot(node)); Assert.Empty(node.ProbeSamples);
    }

    [Fact]
    public async Task Configuration_validation_and_persistence()
    {
        using var admin = await factory.LoginAsync();
        var created = await MasterFactory.DataAsync(await admin.PostAsJsonAsync("/api/nodes", new { publicName = "Config Node" }));
        var target = new ProbeTarget { Id = 5, Name = "线路", Address = "example.com", NodeIds = [created.GetProperty("id").GetInt32()] };
        foreach (var invalid in new[] { target with { Address = "https://example.com/path" }, target with { IntervalSec = 1 }, target with { NodeIds = [] }, target with { TimeoutMs = 60000 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/probes", new[] { invalid })).StatusCode);
        (await admin.PutAsJsonAsync("/api/settings/probes", new[] { target })).EnsureSuccessStatusCode();
        var settings = factory.Services.GetRequiredService<SettingsService>();
        var reloaded = new ProbeService(settings, factory.Services.GetRequiredService<NodeRegistry>());
        Assert.Equal("example.com", Assert.Single(reloaded.Targets).Address);
        Assert.False(settings.Export().ContainsKey("probes"));
        // Disabled targets disappear from agent config and public data.
        (await admin.PutAsJsonAsync("/api/settings/probes", new[] { target with { Enabled = false } })).EnsureSuccessStatusCode();
        Assert.Empty(factory.Services.GetRequiredService<ProbeService>().Snapshot(factory.Services.GetRequiredService<NodeRegistry>().Get(target.NodeIds[0])!));
    }
}
