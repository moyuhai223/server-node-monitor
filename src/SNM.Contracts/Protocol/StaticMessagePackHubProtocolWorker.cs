using MessagePack;
using SNM.Contracts.Dtos;
using SNM.Contracts.Formatters;
using SNM.Contracts.Protocol.Vendored;

namespace SNM.Contracts.Protocol;

/// <summary>
/// Reflection-free replacement for the official DefaultMessagePackHubProtocolWorker: identical framing (inherited from the
/// vendored worker) but argument/result (de)serialization goes through a closed static type switch instead of the
/// non-generic MessagePackSerializer.Serialize(Type, ...) entry points (which need Reflection.Emit / MakeGenericMethod).
/// Supports exactly the types exchanged on /hubs/agent plus a few primitives. Anything else throws
/// NotSupportedException naming the type so that a missing entry fails fast in tests.
/// </summary>
internal sealed class StaticMessagePackHubProtocolWorker : MessagePackHubProtocolWorker
{
    protected override object? DeserializeObject(ref MessagePackReader reader, Type type, string field)
    {
        try
        {
            return Read(ref reader, type);
        }
        catch (NotSupportedException)
        {
            throw; // programming error: type missing from the switch; keep the informative message
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Deserializing object of the `{type.Name}` type for '{field}' failed.", ex);
        }
    }

    protected override void Serialize(ref MessagePackWriter writer, Type type, object value)
        => Write(ref writer, value);

    internal static object? Read(ref MessagePackReader reader, Type type)
    {
        if (type == typeof(AgentConfigDto)) return AgentConfigDtoFormatter.Read(ref reader);
        if (type == typeof(HeartbeatDto)) return HeartbeatDtoFormatter.Read(ref reader);
        if (type == typeof(RegisterDto)) return RegisterDtoFormatter.Read(ref reader);
        if (type == typeof(StatusReportDto)) return StatusReportDtoFormatter.Read(ref reader);
        if (type == typeof(DiskInfoDto)) return DiskInfoDtoFormatter.Read(ref reader);
        if (type == typeof(string)) return reader.ReadString();
        if (type == typeof(int)) return reader.ReadInt32();
        if (type == typeof(long)) return reader.ReadInt64();
        if (type == typeof(uint)) return reader.ReadUInt32();
        if (type == typeof(ulong)) return reader.ReadUInt64();
        if (type == typeof(ushort)) return reader.ReadUInt16();
        if (type == typeof(bool)) return reader.ReadBoolean();
        if (type == typeof(double)) return reader.ReadDouble();
        if (type == typeof(byte[])) return MsgPack.ReadBytes(ref reader);
        if (type == typeof(string[])) return MsgPack.ReadStringArray(ref reader);
        if (type == typeof(object)) { reader.Skip(); return null; }
        throw new NotSupportedException(
            $"StaticMessagePackHubProtocolWorker has no static formatter for '{type.FullName}'. Add it to the type switch (see the checklist in AgentFormatters.cs).");
    }

    internal static void Write(ref MessagePackWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNil(); break;
            case HeartbeatDto v: HeartbeatDtoFormatter.Write(ref writer, v); break;
            case RegisterDto v: RegisterDtoFormatter.Write(ref writer, v); break;
            case StatusReportDto v: StatusReportDtoFormatter.Write(ref writer, v); break;
            case AgentConfigDto v: AgentConfigDtoFormatter.Write(ref writer, v); break;
            case DiskInfoDto v: DiskInfoDtoFormatter.Write(ref writer, v); break;
            case string v: writer.Write(v); break;
            case int v: writer.Write(v); break;
            case long v: writer.Write(v); break;
            case uint v: writer.Write(v); break;
            case ulong v: writer.Write(v); break;
            case ushort v: writer.Write(v); break;
            case bool v: writer.Write(v); break;
            case double v: writer.Write(v); break;
            case byte[] v: writer.Write(v); break;
            case string[] v: MsgPack.WriteStringArray(ref writer, v); break;
            default:
                throw new NotSupportedException(
                    $"StaticMessagePackHubProtocolWorker has no static formatter for '{value.GetType().FullName}'. Add it to the type switch (see the checklist in AgentFormatters.cs).");
        }
    }
}
