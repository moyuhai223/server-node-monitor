using MessagePack;
using SNM.Contracts.Dtos;
using SNM.Master.Data.Entities;
using SNM.Master.Runtime;

namespace SNM.Master.Tests;

public class PublicPushProjectionTests
{
    [Fact]
    public void NextWireMessagesFollowVisibilityChangesInBothDirections()
    {
        var (settings, builder, visible) = PublicFieldsContractTests.Scene();
        var privateNode = new NodeRuntime(new Node { Id = 2, PublicVisible = false });
        foreach (var enabled in new[] { true, false, true })
        {
            settings.Snapshot.ShowTraffic = enabled;
            settings.Snapshot.ShowSpecs = enabled;
            var batch = builder.PublicBatch([visible, privateNode], DateTime.UtcNow);
            var metadata = builder.PublicMetadata();
            var item = Assert.Single(MessagePackSerializer.Deserialize<PublicBatchDto>(MessagePackSerializer.Serialize(batch)).Items);
            var row = Assert.Single(MessagePackSerializer.Deserialize<PublicNodeDto[]>(MessagePackSerializer.Serialize(metadata)));
            Assert.Equal(visible.Id, item.Id);
            Assert.Equal(enabled ? 300UL : 0UL, item.TrafficUsedBytes);
            Assert.Equal(19UL, item.RxBps);
            Assert.Equal(enabled ? 8 : 0, row.Cores);
            Assert.Equal(enabled ? 5000UL : 0UL, row.TrafficLimitBytes);
            Assert.Equal(item.TrafficUsedBytes, row.Live.TrafficUsedBytes);
            Assert.Null(row.Hist);
        }
    }
}
