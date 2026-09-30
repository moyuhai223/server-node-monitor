using System.Buffers;
using MessagePack;
using SNM.Contracts.Dtos;
using SNM.Contracts.Formatters;

namespace SNM.Contracts.Tests;

public class ProbeContractTests
{
    [Fact]
    public void Config_and_nested_targets_match_official_serializer()
    {
        var dto = new ProbeConfigDto { Targets = [new ProbeTargetDto { Id = 31, Revision = "abc", Kind = 1, Address = "example.com", Port = 443, IntervalSec = 30, TimeoutMs = 2000 }] };
        var buffer = new ArrayBufferWriter<byte>(); var writer = new MessagePackWriter(buffer);
        ProbeConfigDtoFormatter.Write(ref writer, dto); writer.Flush();
        Assert.Equal(MessagePackSerializer.Serialize(dto), buffer.WrittenSpan.ToArray());
        var reader = new MessagePackReader(buffer.WrittenMemory);
        var decoded = ProbeConfigDtoFormatter.Read(ref reader);
        Assert.Equal("example.com", Assert.Single(decoded.Targets).Address);
        Assert.Equal(2000, decoded.Targets[0].TimeoutMs);
    }

    [Theory]
    [InlineData(0, 12345)]
    [InlineData(1, -1)]
    [InlineData(2, -1)]
    [InlineData(3, -1)]
    public void Results_match_official_serializer(byte status, int us)
    {
        var dto = new ProbeResultDto { Id = 1, Revision = "revision", Status = status, Microseconds = us };
        var buffer = new ArrayBufferWriter<byte>(); var writer = new MessagePackWriter(buffer);
        ProbeResultDtoFormatter.Write(ref writer, dto); writer.Flush();
        Assert.Equal(MessagePackSerializer.Serialize(dto), buffer.WrittenSpan.ToArray());
        var reader = new MessagePackReader(buffer.WrittenMemory);
        var decoded = ProbeResultDtoFormatter.Read(ref reader);
        Assert.Equal(us, decoded.Microseconds); Assert.Equal(status, decoded.Status);
    }

    [Fact]
    public void Readers_accept_short_and_future_arrays_and_limit_target_count()
    {
        var buffer = new ArrayBufferWriter<byte>(); var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(1); writer.Write(42); writer.Flush();
        var reader = new MessagePackReader(buffer.WrittenMemory);
        Assert.Equal(-1, ProbeResultDtoFormatter.Read(ref reader).Microseconds);
        buffer = new ArrayBufferWriter<byte>(); writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(5); writer.Write(42); writer.Write("rev"); writer.Write((byte)0); writer.Write(12); writer.Write("future"); writer.Flush();
        reader = new MessagePackReader(buffer.WrittenMemory);
        Assert.Equal(12, ProbeResultDtoFormatter.Read(ref reader).Microseconds); Assert.True(reader.End);
        buffer = new ArrayBufferWriter<byte>(); writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(1); writer.WriteArrayHeader(17);
        for (var i = 0; i < 17; i++) writer.WriteNil();
        writer.Flush();
        Assert.Throws<InvalidDataException>(() => ReadConfig(buffer.WrittenMemory));
    }
    private static void ReadConfig(ReadOnlyMemory<byte> bytes) { var reader = new MessagePackReader(bytes); ProbeConfigDtoFormatter.Read(ref reader); }
}
