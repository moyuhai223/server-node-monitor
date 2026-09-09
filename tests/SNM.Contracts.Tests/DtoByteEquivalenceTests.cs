using MessagePack;
using MessagePack.Resolvers;
using SNM.Contracts.Dtos;
using SNM.Contracts.Formatters;
using Xunit.Abstractions;

namespace SNM.Contracts.Tests;

/// <summary>
/// Hand-written formatters must be byte-identical to what the Master's reflection-based resolver produces
/// (StandardResolver, the options the Master configures for the agent hub).
/// </summary>
public sealed class DtoByteEquivalenceTests(ITestOutputHelper output)
{
    private static readonly MessagePackSerializerOptions Oracle =
        MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance).WithSecurity(MessagePackSecurity.UntrustedData);

    private delegate void Writer<in T>(ref MessagePackWriter w, T value);

    private static byte[] Static<T>(Writer<T> write, T value)
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buf);
        write(ref w, value);
        w.Flush();
        return buf.WrittenSpan.ToArray();
    }

    public static IEnumerable<object?[]> Heartbeats() =>
    [
        ["typical", Samples.TypicalHeartbeat()],
        ["max", Samples.MaxHeartbeat()],
        ["min(null disks)", Samples.MinHeartbeat()],
        ["emptyDisks", Samples.EmptyDisksHeartbeat()],
        ["null", null],
    ];

    [Theory]
    [MemberData(nameof(Heartbeats))]
    public void Heartbeat_static_bytes_equal_reflection_bytes(string name, HeartbeatDto? dto)
    {
        var expected = MessagePackSerializer.Serialize(dto, Oracle);
        var actual = Static<HeartbeatDto?>(HeartbeatDtoFormatter.Write, dto);
        output.WriteLine($"HeartbeatDto[{name}] = {actual.Length} bytes: {Convert.ToHexString(actual)}");
        Assert.Equal(expected, actual);

        var reader = new MessagePackReader(actual);
        Assert.True(Samples.Equal(HeartbeatDtoFormatter.Read(ref reader), dto));
        Assert.True(reader.End);
    }

    [Fact]
    public void Typical_heartbeat_fits_the_byte_budget()
    {
        var bytes = Static<HeartbeatDto?>(HeartbeatDtoFormatter.Write, Samples.TypicalHeartbeat());
        output.WriteLine($"typical heartbeat DTO = {bytes.Length} bytes");
        Assert.InRange(bytes.Length, 20, 40);
    }

    public static IEnumerable<object?[]> Registers() =>
    [
        ["typical", Samples.TypicalRegister()],
        ["minimal", Samples.MinimalRegister()],
        ["unicode", Samples.UnicodeRegister()],
        ["null", null],
    ];

    [Theory]
    [MemberData(nameof(Registers))]
    public void Register_static_bytes_equal_reflection_bytes(string name, RegisterDto? dto)
    {
        var expected = MessagePackSerializer.Serialize(dto, Oracle);
        var actual = Static<RegisterDto?>(RegisterDtoFormatter.Write, dto);
        output.WriteLine($"RegisterDto[{name}] = {actual.Length} bytes");
        Assert.Equal(expected, actual);

        var reader = new MessagePackReader(actual);
        Assert.True(Samples.Equal(RegisterDtoFormatter.Read(ref reader), dto));
        Assert.True(reader.End);
    }

    public static IEnumerable<object?[]> Statuses() =>
    [
        ["typical", Samples.TypicalStatus()],
        ["nulls", Samples.NullsStatus()],
        ["null", null],
    ];

    [Theory]
    [MemberData(nameof(Statuses))]
    public void Status_static_bytes_equal_reflection_bytes(string name, StatusReportDto? dto)
    {
        _ = name;
        var expected = MessagePackSerializer.Serialize(dto, Oracle);
        var actual = Static<StatusReportDto?>(StatusReportDtoFormatter.Write, dto);
        Assert.Equal(expected, actual);

        var reader = new MessagePackReader(actual);
        Assert.True(Samples.Equal(StatusReportDtoFormatter.Read(ref reader), dto));
        Assert.True(reader.End);
    }

    public static IEnumerable<object?[]> Configs() =>
    [
        ["default", Samples.DefaultConfig()],
        ["max", Samples.MaxConfig()],
        ["null", null],
    ];

    [Theory]
    [MemberData(nameof(Configs))]
    public void Config_static_bytes_equal_reflection_bytes(string name, AgentConfigDto? dto)
    {
        _ = name;
        var expected = MessagePackSerializer.Serialize(dto, Oracle);
        var actual = Static<AgentConfigDto?>(AgentConfigDtoFormatter.Write, dto);
        Assert.Equal(expected, actual);

        var reader = new MessagePackReader(actual);
        Assert.True(Samples.Equal(AgentConfigDtoFormatter.Read(ref reader), dto));
        Assert.True(reader.End);
    }

    [Fact]
    public void Disk_static_bytes_equal_reflection_bytes()
    {
        var dto = Samples.Disk();
        Assert.Equal(MessagePackSerializer.Serialize(dto, Oracle), Static<DiskInfoDto?>(DiskInfoDtoFormatter.Write, dto));
        Assert.Equal(MessagePackSerializer.Serialize<DiskInfoDto?>(null, Oracle), Static<DiskInfoDto?>(DiskInfoDtoFormatter.Write, null));
    }

    [Fact]
    public void Reflection_bytes_are_read_by_static_reader()
    {
        // The Master serializes AgentConfigDto with the reflection resolver; the agent reads it statically.
        var cfg = Samples.MaxConfig();
        var bytes = MessagePackSerializer.Serialize(cfg, Oracle);
        var reader = new MessagePackReader(bytes);
        Assert.True(Samples.Equal(AgentConfigDtoFormatter.Read(ref reader), cfg));
    }

    [Fact]
    public void Reader_tolerates_shorter_and_longer_arrays()
    {
        // Shorter array (older agent): missing trailing fields default.
        var buf = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buf);
        w.WriteArrayHeader(3);
        w.Write((uint)7); w.Write((uint)2000); w.Write((ushort)500);
        w.Flush();
        var r = new MessagePackReader(buf.WrittenMemory);
        var hb = HeartbeatDtoFormatter.Read(ref r)!;
        Assert.Equal(7u, hb.Seq);
        Assert.Equal(500, hb.Cpu);
        Assert.Null(hb.DiskUsedMb);
        Assert.Equal(0u, hb.NetRxBytes);

        // Longer array (newer agent): extra trailing fields are skipped.
        buf = new ArrayBufferWriter<byte>();
        w = new MessagePackWriter(buf);
        w.WriteArrayHeader(HeartbeatDto.FieldCount + 2);
        HeartbeatDtoFormatterBody(ref w, Samples.TypicalHeartbeat());
        w.Write("future-string-field");
        w.WriteMapHeader(1); w.Write("k"); w.Write(1);
        w.Flush();
        r = new MessagePackReader(buf.WrittenMemory);
        Assert.True(Samples.Equal(HeartbeatDtoFormatter.Read(ref r), Samples.TypicalHeartbeat()));
        Assert.True(r.End);
    }

    private static void HeartbeatDtoFormatterBody(ref MessagePackWriter w, HeartbeatDto v)
    {
        w.Write(v.Seq); w.Write(v.ElapsedMs); w.Write(v.Cpu); w.Write(v.MemUsedMb); w.Write(v.SwapUsedMb);
        w.WriteArrayHeader(v.DiskUsedMb!.Length);
        foreach (var d in v.DiskUsedMb) w.Write(d);
        w.Write(v.NetRxBytes); w.Write(v.NetTxBytes); w.Write(v.Load1);
    }

    [Fact]
    public void Oversized_array_header_is_rejected()
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buf);
        w.WriteArrayHeader(HeartbeatDto.FieldCount);
        w.Write((uint)1); w.Write((uint)1); w.Write((ushort)1); w.Write((uint)1); w.Write((uint)1);
        w.WriteArrayHeader(ProtocolConstants.MaxArrayHeader + 1);
        // Enough trailing bytes so that MessagePackReader's own "remaining bytes >= count" guard passes and our limit is what rejects.
        for (var i = 0; i < ProtocolConstants.MaxArrayHeader + 8; i++) w.WriteNil();
        w.Flush();
        Assert.Throws<InvalidDataException>(() =>
        {
            var r = new MessagePackReader(buf.WrittenMemory);
            HeartbeatDtoFormatter.Read(ref r);
        });
    }
}
