using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

namespace SNM.Master.Tests;

/// <summary>The agent-side protocol (SnmMessagePackHubProtocol) talking to the master's official MessagePack protocol through TestServer.</summary>
public class AgentHubIntegrationTests(MasterFactory factory) : IClassFixture<MasterFactory>
{
    private HubConnection BuildAgentConnection(string key, bool officialProtocol = false)
    {
        var builder = new HubConnectionBuilder()
            .WithUrl(factory.Server.BaseAddress + HubRoutes.Agent.TrimStart('/'), HttpTransportType.LongPolling, o =>
            {
                o.Headers[ProtocolConstants.AgentKeyHeader] = key;
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            });
        if (officialProtocol) builder.AddMessagePackProtocol();
        else builder.Services.Replace(ServiceDescriptor.Singleton<IHubProtocol, SnmMessagePackHubProtocol>());
        return builder.Build();
    }

    private async Task<(int Id, string Key)> CreateNodeAsync(string name, string remark)
    {
        var auth = await factory.LoginAsync();
        var created = await MasterFactory.DataAsync(await auth.PostAsJsonAsync("/api/nodes", new { publicName = name, adminRemark = remark, traffic = new { limitBytes = 10_000_000_000L } }));
        return (created.GetProperty("id").GetInt32(), created.GetProperty("agentKey").GetString()!);
    }

    [Fact]
    public async Task RegisterHeartbeatStatusRoundTrip()
    {
        var (id, key) = await CreateNodeAsync("Agent-RT", "SECRET-REMARK");
        await using var conn = BuildAgentConnection(key);
        AgentConfigDto? pushed = null;
        conn.On<AgentConfigDto>(AgentHubMethods.Configure, c => pushed = c);
        await conn.StartAsync();

        var reg = new RegisterDto
        {
            AgentVersion = "1.0.0-test", Hostname = "secret-host-name", Os = "Ubuntu 24.04", Kernel = "6.8.0", Arch = "x64", CpuModel = "2x Test CPU", CpuCores = 8,
            MemTotalMb = 16000, SwapTotalMb = 1024, Disks = [new DiskInfoDto { Mount = "/", Fs = "ext4", TotalMb = 100_000 }], Ips = ["10.1.2.3", "2001:db8::10"],
            UptimeSec = 3600, Virt = "kvm", IntervalMs = 2000, NetIfs = "eth0",
        };
        var cfg = await conn.InvokeAsync<AgentConfigDto>(AgentHubMethods.Register, reg);
        Assert.Equal(2000, cfg.IntervalMs);
        Assert.Equal(300, cfg.StatusIntervalSec);

        ulong rx = 1_000_000, tx = 500_000;
        for (uint seq = 1; seq <= 3; seq++)
        {
            await conn.SendAsync(AgentHubMethods.Heartbeat, new HeartbeatDto
            {
                Seq = seq, ElapsedMs = seq == 1 ? 0u : 2000u, Cpu = (ushort)(100 * seq), MemUsedMb = 4000, SwapUsedMb = 10, DiskUsedMb = [40_000],
                NetRxBytes = rx, NetTxBytes = tx, Load1 = 120,
            });
            rx += 2_000_000; tx += 1_000_000;
        }
        await conn.SendAsync(AgentHubMethods.ReportStatus, new StatusReportDto { Ips = ["10.1.2.3"], Disks = reg.Disks, UptimeSec = 3700, ProcCount = 123, NetIfs = "eth0", MemTotalMb = 16000 });

        var registry = factory.Services.GetRequiredService<NodeRegistry>();
        var node = registry.Get(id)!;
        await WaitUntilAsync(() => node.LastSeq == 3 && node.ProcCount == 123, TimeSpan.FromSeconds(10));
        Assert.True(node.Connected);
        Assert.Equal(NodeStatus.Online, node.Status);
        Assert.Equal(300, node.Live!.Cpu);
        Assert.Equal(4000u, node.Live.MemUsedMb);
        // deltas: 2 x 2,000,000 rx and 2 x 1,000,000 tx after the baseline sample
        Assert.Equal(4_000_000, node.Traffic.PerRx);
        Assert.Equal(2_000_000, node.Traffic.PerTx);
        Assert.Equal(1_000_000u, node.Live.RxBps);   // 2,000,000 B / 2 s
        Assert.Equal(3, node.History.Snapshot().Length);

        // REST view merges memory + database
        var auth = await factory.LoginAsync();
        var detail = await MasterFactory.DataAsync(await auth.GetAsync($"/api/nodes/{id}"));
        Assert.True(detail.GetProperty("state").GetProperty("connected").GetBoolean());
        Assert.Equal("secret-host-name", detail.GetProperty("hardware").GetProperty("hostname").GetString());
        Assert.Equal("2x Test CPU", detail.GetProperty("hardware").GetProperty("cpuModel").GetString());
        Assert.Equal(300, detail.GetProperty("live").GetProperty("cpuPermille").GetInt32());
        Assert.Contains(detail.GetProperty("ips").EnumerateArray(), ip => ip.GetProperty("address").GetString() == "10.1.2.3");
        Assert.Equal(6_000_000, detail.GetProperty("traffic").GetProperty("billedBytes").GetInt64());

        // interval change pushes "configure" to the live connection
        (await auth.PatchJsonAsync($"/api/nodes/{id}", new { intervalMs = 5000 })).EnsureSuccessStatusCode();
        await WaitUntilAsync(() => pushed is not null, TimeSpan.FromSeconds(10));
        Assert.Equal(5000, pushed!.IntervalMs);

        // public snapshot must not leak hostname / remark / IPs
        var builder = factory.Services.GetRequiredService<LiveSnapshotBuilder>();
        var snapshot = builder.PublicSnapshot(DateTime.UtcNow);
        var bytes = MessagePackSerializer.Serialize(snapshot, MessagePackSerializerOptions.Standard);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("secret-host-name", text);
        Assert.DoesNotContain("SECRET-REMARK", text);
        Assert.DoesNotContain("10.1.2.3", text);
        Assert.DoesNotContain("snmk_", text);
        Assert.Contains("Agent-RT", text);

        await conn.StopAsync();
        await WaitUntilAsync(() => !node.Connected, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OfficialMessagePackClientIsWireCompatible()
    {
        var (id, key) = await CreateNodeAsync("Agent-Official", "");
        await using var conn = BuildAgentConnection(key, officialProtocol: true);
        await conn.StartAsync();
        var cfg = await conn.InvokeAsync<AgentConfigDto>(AgentHubMethods.Register, new RegisterDto { Hostname = "h", AgentVersion = "x" });
        Assert.Equal(2000, cfg.IntervalMs);
        await conn.SendAsync(AgentHubMethods.Heartbeat, new HeartbeatDto { Seq = 1, Cpu = 50, NetRxBytes = 10, NetTxBytes = 10 });
        var node = factory.Services.GetRequiredService<NodeRegistry>().Get(id)!;
        await WaitUntilAsync(() => node.LastSeq == 1, TimeSpan.FromSeconds(10));
        Assert.Equal(50, node.Live!.Cpu);
    }

    [Fact]
    public async Task UnknownKeyIsRejected()
    {
        await using var conn = BuildAgentConnection("snmk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => conn.StartAsync());
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task PublicHubSendsSnapshotToAnonymousBrowsers()
    {
        await CreateNodeAsync("Public-Visible", "hidden remark");
        var received = new TaskCompletionSource<PublicSnapshotDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conn = new HubConnectionBuilder()
            .WithUrl(factory.Server.BaseAddress + HubRoutes.Public.TrimStart('/'), HttpTransportType.LongPolling, o => o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler())
            .AddMessagePackProtocol()
            .Build();
        conn.On<PublicSnapshotDto>(PublicHubMethods.Snapshot, s => received.TrySetResult(s));
        await conn.StartAsync();
        var snapshot = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(snapshot.Nodes, n => n.Name == "Public-Visible");
        Assert.True(snapshot.Site.OfflineTimeoutSec > 0);
        var again = await conn.InvokeAsync<PublicSnapshotDto>(PublicHubMethods.GetSnapshot);
        Assert.Equal(snapshot.Nodes.Length, again.Nodes.Length);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met within " + timeout);
            await Task.Delay(50);
        }
    }
}
