using Microsoft.Extensions.Logging.Abstractions;
using SNM.Contracts.Dtos;
using SNM.Master.Data.Entities;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Tests;

public class PublicFieldsContractTests
{
    internal static (SettingsService Settings, LiveSnapshotBuilder Builder, NodeRuntime Node) Scene()
    {
        var settings = new SettingsService(null!, NullLogger<SettingsService>.Instance);
        var registry = new NodeRegistry(null!, settings, NullLogger<NodeRegistry>.Instance);
        var builder = new LiveSnapshotBuilder(registry, settings, new ProbeService(settings, registry));
        var node = registry.Upsert(new Node { Id = 1, AgentKey = "fixture", CpuCores = 8, MemTotalMb = 4096, TrafficLimitBytes = 5000 });
        node.Disks = [new DiskInfoDto { TotalMb = 8000 }];
        node.Live = new LiveSample(500, 1024, 0, [4000], 19, 31, 0, 200, DateTime.UtcNow);
        node.Traffic.PerRx = 100; node.Traffic.PerTx = 200;
        node.History.Add(new LivePoint(1, 500, 250, 19, 31));
        return (settings, builder, node);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EachSwitchAppliesToSnapshotMetadataAndLiveButNotUtilizationOrAdmin(bool specs, bool traffic)
    {
        var (settings, builder, node) = Scene();
        settings.Snapshot.ShowSpecs = specs; settings.Snapshot.ShowTraffic = traffic;
        var snapshot = builder.PublicSnapshot(DateTime.UtcNow);
        Assert.Equal(specs, snapshot.Site.ShowSpecs); Assert.Equal(traffic, snapshot.Site.ShowTraffic);
        var full = Assert.Single(snapshot.Nodes);
        var metadata = builder.PublicNode(node, withHistory: false);
        Assert.Null(metadata.Hist);
        foreach (var dto in new[] { full, metadata })
        {
            Assert.Equal(specs ? 8 : 0, dto.Cores);
            Assert.Equal(specs ? 4096u : 0u, dto.MemTotalMb);
            Assert.Equal(specs ? 8000UL : 0UL, dto.DiskTotalMb);
            Assert.Equal(traffic ? 5000UL : 0UL, dto.TrafficLimitBytes);
        }
        foreach (var live in new[] { full.Live, metadata.Live, builder.PublicLive(node) })
        {
            Assert.Equal(traffic ? 300UL : 0UL, live.TrafficUsedBytes);
            Assert.Equal(500, live.Cpu); Assert.Equal(250, live.Mem); Assert.Equal(500, live.Disk);
            Assert.Equal(19UL, live.RxBps); Assert.Equal(31UL, live.TxBps);
        }
        Assert.Equal(19UL, Assert.Single(full.Hist!.Rx));
        Assert.Equal(300UL, builder.AdminLive(node).TrafficUsedBytes);
        Assert.Equal(1024u, builder.AdminLive(node).MemUsedMb);
    }
}
