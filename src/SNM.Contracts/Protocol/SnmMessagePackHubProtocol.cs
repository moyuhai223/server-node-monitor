using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace SNM.Contracts.Protocol;

/// <summary>
/// Native-AOT-safe SignalR hub protocol, wire-compatible with the official MessagePack hub protocol
/// (name "messagepack", version 2, binary transfer format) but performing no reflection at all.
/// The Master and browsers keep using the official implementations; only the agent swaps this in:
/// <code>builder.Services.Replace(ServiceDescriptor.Singleton&lt;IHubProtocol, SnmMessagePackHubProtocol&gt;());</code>
/// </summary>
public sealed class SnmMessagePackHubProtocol : IHubProtocol
{
    public const string ProtocolName = "messagepack";
    public const int ProtocolVersion = 2;

    private readonly StaticMessagePackHubProtocolWorker _worker = new();

    public string Name => ProtocolName;
    public int Version => ProtocolVersion;
    public TransferFormat TransferFormat => TransferFormat.Binary;

    public bool IsVersionSupported(int version) => version <= Version;

    public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, [NotNullWhen(true)] out HubMessage? message)
        => _worker.TryParseMessage(ref input, binder, out message);

    public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
        => _worker.WriteMessage(message, output);

    public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
        => _worker.GetMessageBytes(message);
}
