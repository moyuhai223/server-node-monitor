using System.Buffers;
using MessagePack;
using SNM.Contracts.Dtos;

namespace SNM.Contracts.Formatters;

// Reflection-free formatters used by the Native AOT agent. Rules:
//  * only MessagePackWriter/MessagePackReader APIs; never MessagePackSerializer, resolvers or MessagePackSerializerOptions;
//  * Write() emits exactly what DynamicObjectResolver emits for the attributed class (array header = field count,
//    smallest integer encoding, nil for null arrays/objects);
//  * Read() tolerates shorter (defaults) and longer (Skip) arrays and nil (null).
// CHECKLIST when adding a DTO: (1) DTO with [MessagePackObject]/[Key(n)] + FieldCount, (2) formatter here,
// (3) one case in StaticMessagePackHubProtocolWorker.Write and one branch in Read,
// (4) byte-equality test vs MessagePackSerializer.Serialize(dto, Standard) in tests/SNM.Contracts.Tests.

internal static class MsgPack
{
    public static void WriteStringArray(ref MessagePackWriter w, string[]? a)
    {
        if (a is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(a.Length);
        foreach (var s in a) w.Write(s);
    }

    public static string[]? ReadStringArray(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        var n = ReadArrayHeaderChecked(ref r);
        var a = new string[n];
        for (var i = 0; i < n; i++) a[i] = r.ReadString() ?? "";
        return a;
    }

    public static void WriteUInt32Array(ref MessagePackWriter w, uint[]? a)
    {
        if (a is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(a.Length);
        foreach (var v in a) w.Write(v);
    }

    public static uint[]? ReadUInt32Array(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        var n = ReadArrayHeaderChecked(ref r);
        var a = new uint[n];
        for (var i = 0; i < n; i++) a[i] = r.ReadUInt32();
        return a;
    }

    public static byte[]? ReadBytes(ref MessagePackReader r)
    {
        // Same semantics as MessagePack.Formatters.ByteArrayFormatter (nil => null).
        var seq = r.ReadBytes();
        return seq?.ToArray();
    }

    public static int ReadArrayHeaderChecked(ref MessagePackReader r)
    {
        var n = r.ReadArrayHeader();
        if (n > ProtocolConstants.MaxArrayHeader)
        {
            throw new InvalidDataException($"Array header {n} exceeds the protocol limit {ProtocolConstants.MaxArrayHeader}.");
        }
        return n;
    }
}

public static class DiskInfoDtoFormatter
{
    public static void Write(ref MessagePackWriter w, DiskInfoDto? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(DiskInfoDto.FieldCount);
        w.Write(v.Mount);
        w.Write(v.Fs);
        w.Write(v.TotalMb);
    }

    public static DiskInfoDto? Read(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        var n = MsgPack.ReadArrayHeaderChecked(ref r);
        var v = new DiskInfoDto();
        for (var i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.Mount = r.ReadString() ?? ""; break;
                case 1: v.Fs = r.ReadString() ?? ""; break;
                case 2: v.TotalMb = r.ReadUInt32(); break;
                default: r.Skip(); break;
            }
        }
        return v;
    }

    public static void WriteArray(ref MessagePackWriter w, DiskInfoDto[]? a)
    {
        if (a is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(a.Length);
        foreach (var d in a) Write(ref w, d);
    }

    public static DiskInfoDto[]? ReadArray(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        var n = MsgPack.ReadArrayHeaderChecked(ref r);
        var a = new DiskInfoDto[n];
        for (var i = 0; i < n; i++) a[i] = Read(ref r) ?? new DiskInfoDto();
        return a;
    }
}

public static class HeartbeatDtoFormatter
{
    public static void Write(ref MessagePackWriter w, HeartbeatDto? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(HeartbeatDto.FieldCount);
        w.Write(v.Seq);
        w.Write(v.ElapsedMs);
        w.Write(v.Cpu);
        w.Write(v.MemUsedMb);
        w.Write(v.SwapUsedMb);
        MsgPack.WriteUInt32Array(ref w, v.DiskUsedMb);
        w.Write(v.NetRxBytes);
        w.Write(v.NetTxBytes);
        w.Write(v.Load1);
    }

    public static HeartbeatDto? Read(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        var n = MsgPack.ReadArrayHeaderChecked(ref r);
        var v = new HeartbeatDto();
        for (var i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.Seq = r.ReadUInt32(); break;
                case 1: v.ElapsedMs = r.ReadUInt32(); break;
                case 2: v.Cpu = r.ReadUInt16(); break;
                case 3: v.MemUsedMb = r.ReadUInt32(); break;
                case 4: v.SwapUsedMb = r.ReadUInt32(); break;
                case 5: v.DiskUsedMb = MsgPack.ReadUInt32Array(ref r); break;
                case 6: v.NetRxBytes = r.ReadUInt64(); break;
                case 7: v.NetTxBytes = r.ReadUInt64(); break;
                case 8: v.Load1 = r.ReadUInt16(); break;
                default: r.Skip(); break;
            }
        }
        return v;
    }
}

public static class RegisterDtoFormatter
{
    public static void Write(ref MessagePackWriter w, RegisterDto? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(RegisterDto.FieldCount);
        w.Write(v.ProtocolVersion);
        w.Write(v.AgentVersion);
        w.Write(v.Hostname);
        w.Write(v.Os);
        w.Write(v.Kernel);
        w.Write(v.Arch);
        w.Write(v.CpuModel);
        w.Write(v.CpuCores);
        w.Write(v.MemTotalMb);
        w.Write(v.SwapTotalMb);
        DiskInfoDtoFormatter.WriteArray(ref w, v.Disks);
        MsgPack.WriteStringArray(ref w, v.Ips);
        w.Write(v.UptimeSec);
        w.Write(v.Virt);
        w.Write(v.IntervalMs);
        w.Write(v.NetIfs);
    }

    public static RegisterDto? Read(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        var n = MsgPack.ReadArrayHeaderChecked(ref r);
        var v = new RegisterDto();
        for (var i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.ProtocolVersion = r.ReadUInt16(); break;
                case 1: v.AgentVersion = r.ReadString() ?? ""; break;
                case 2: v.Hostname = r.ReadString() ?? ""; break;
                case 3: v.Os = r.ReadString() ?? ""; break;
                case 4: v.Kernel = r.ReadString() ?? ""; break;
                case 5: v.Arch = r.ReadString() ?? ""; break;
                case 6: v.CpuModel = r.ReadString() ?? ""; break;
                case 7: v.CpuCores = r.ReadUInt16(); break;
                case 8: v.MemTotalMb = r.ReadUInt32(); break;
                case 9: v.SwapTotalMb = r.ReadUInt32(); break;
                case 10: v.Disks = DiskInfoDtoFormatter.ReadArray(ref r); break;
                case 11: v.Ips = MsgPack.ReadStringArray(ref r); break;
                case 12: v.UptimeSec = r.ReadUInt32(); break;
                case 13: v.Virt = r.ReadString() ?? ""; break;
                case 14: v.IntervalMs = r.ReadUInt16(); break;
                case 15: v.NetIfs = r.ReadString() ?? ""; break;
                default: r.Skip(); break;
            }
        }
        return v;
    }
}

public static class StatusReportDtoFormatter
{
    public static void Write(ref MessagePackWriter w, StatusReportDto? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(StatusReportDto.FieldCount);
        MsgPack.WriteStringArray(ref w, v.Ips);
        DiskInfoDtoFormatter.WriteArray(ref w, v.Disks);
        w.Write(v.UptimeSec);
        w.Write(v.ProcCount);
        w.Write(v.NetIfs);
        w.Write(v.MemTotalMb);
    }

    public static StatusReportDto? Read(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        var n = MsgPack.ReadArrayHeaderChecked(ref r);
        var v = new StatusReportDto();
        for (var i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.Ips = MsgPack.ReadStringArray(ref r); break;
                case 1: v.Disks = DiskInfoDtoFormatter.ReadArray(ref r); break;
                case 2: v.UptimeSec = r.ReadUInt32(); break;
                case 3: v.ProcCount = r.ReadUInt16(); break;
                case 4: v.NetIfs = r.ReadString() ?? ""; break;
                case 5: v.MemTotalMb = r.ReadUInt32(); break;
                default: r.Skip(); break;
            }
        }
        return v;
    }
}

public static class AgentConfigDtoFormatter
{
    public static void Write(ref MessagePackWriter w, AgentConfigDto? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(AgentConfigDto.FieldCount);
        w.Write(v.IntervalMs);
        w.Write(v.StatusIntervalSec);
    }

    public static AgentConfigDto? Read(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        var n = MsgPack.ReadArrayHeaderChecked(ref r);
        var v = new AgentConfigDto();
        for (var i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.IntervalMs = r.ReadUInt16(); break;
                case 1: v.StatusIntervalSec = r.ReadUInt16(); break;
                default: r.Skip(); break;
            }
        }
        return v;
    }
}
