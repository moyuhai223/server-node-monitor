using MessagePack;
using SNM.Contracts.Dtos;

namespace SNM.Contracts.Formatters;

public static class ProbeTargetDtoFormatter
{
    public static void Write(ref MessagePackWriter w, ProbeTargetDto v)
    {
        w.WriteArrayHeader(ProbeTargetDto.FieldCount);
        w.Write(v.Id); w.Write(v.Revision); w.Write(v.Kind); w.Write(v.Address);
        w.Write(v.Port); w.Write(v.IntervalSec); w.Write(v.TimeoutMs);
    }
    public static ProbeTargetDto Read(ref MessagePackReader r)
    {
        var n = MsgPack.ReadArrayHeaderChecked(ref r);
        var v = new ProbeTargetDto();
        for (var i = 0; i < n; i++) switch (i)
        {
            case 0: v.Id = r.ReadInt32(); break;
            case 1: v.Revision = r.ReadString() ?? ""; break;
            case 2: v.Kind = r.ReadByte(); break;
            case 3: v.Address = r.ReadString() ?? ""; break;
            case 4: v.Port = r.ReadUInt16(); break;
            case 5: v.IntervalSec = r.ReadUInt16(); break;
            case 6: v.TimeoutMs = r.ReadUInt16(); break;
            default: r.Skip(); break;
        }
        return v;
    }
}

public static class ProbeConfigDtoFormatter
{
    public static void Write(ref MessagePackWriter w, ProbeConfigDto v)
    {
        w.WriteArrayHeader(ProbeConfigDto.FieldCount);
        w.WriteArrayHeader(v.Targets.Length);
        foreach (var t in v.Targets) ProbeTargetDtoFormatter.Write(ref w, t);
    }
    public static ProbeConfigDto Read(ref MessagePackReader r)
    {
        var n = MsgPack.ReadArrayHeaderChecked(ref r);
        var v = new ProbeConfigDto();
        for (var i = 0; i < n; i++)
        {
            if (i != 0) { r.Skip(); continue; }
            var count = MsgPack.ReadArrayHeaderChecked(ref r);
            if (count > 16) throw new InvalidDataException("Too many probe targets");
            v.Targets = new ProbeTargetDto[count];
            for (var j = 0; j < count; j++) v.Targets[j] = ProbeTargetDtoFormatter.Read(ref r);
        }
        return v;
    }
}

public static class ProbeResultDtoFormatter
{
    public static void Write(ref MessagePackWriter w, ProbeResultDto v)
    {
        w.WriteArrayHeader(ProbeResultDto.FieldCount);
        w.Write(v.Id); w.Write(v.Revision); w.Write(v.Status); w.Write(v.Microseconds);
    }
    public static ProbeResultDto Read(ref MessagePackReader r)
    {
        var n = MsgPack.ReadArrayHeaderChecked(ref r);
        var v = new ProbeResultDto();
        for (var i = 0; i < n; i++) switch (i)
        {
            case 0: v.Id = r.ReadInt32(); break;
            case 1: v.Revision = r.ReadString() ?? ""; break;
            case 2: v.Status = r.ReadByte(); break;
            case 3: v.Microseconds = r.ReadInt32(); break;
            default: r.Skip(); break;
        }
        return v;
    }
}
