using MessagePack;

namespace SNM.Contracts.Dtos;

[MessagePackObject]
public sealed class ProbeTargetDto
{
    public const int FieldCount = 7;
    [Key(0)] public int Id { get; set; }
    [Key(1)] public string Revision { get; set; } = "";
    [Key(2)] public byte Kind { get; set; } // 0 = ICMP, 1 = TCP
    [Key(3)] public string Address { get; set; } = "";
    [Key(4)] public ushort Port { get; set; }
    [Key(5)] public ushort IntervalSec { get; set; }
    [Key(6)] public ushort TimeoutMs { get; set; }
}

[MessagePackObject]
public sealed class ProbeConfigDto
{
    public const int FieldCount = 1;
    [Key(0)] public ProbeTargetDto[] Targets { get; set; } = [];
}

[MessagePackObject]
public sealed class ProbeResultDto
{
    public const int FieldCount = 4;
    [Key(0)] public int Id { get; set; }
    [Key(1)] public string Revision { get; set; } = "";
    [Key(2)] public byte Status { get; set; } // 0 success, 1 timeout, 2 error, 3 unsupported
    [Key(3)] public int Microseconds { get; set; } = -1;
}

[MessagePackObject]
public sealed class PublicProbePointDto
{
    [Key("ts")] public long Ts { get; set; }
    [Key("state")] public byte State { get; set; }
    [Key("us")] public int DurationUs { get; set; }
}

[MessagePackObject]
public sealed class PublicProbeDto
{
    [Key("id")] public int Id { get; set; }
    [Key("name")] public string Name { get; set; } = "";
    [Key("kind")] public byte Kind { get; set; }
    [Key("interval")] public int IntervalSec { get; set; }
    [Key("points")] public PublicProbePointDto[] Points { get; set; } = [];
}

[MessagePackObject]
public sealed class PublicProbeBatchDto
{
    [Key("id")] public int Id { get; set; }
    [Key("probes")] public PublicProbeDto[] Probes { get; set; } = [];
}
