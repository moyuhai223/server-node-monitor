using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using SNM.Contracts.Dtos;
using SNM.Contracts.Protocol;
using Xunit.Abstractions;

namespace SNM.Contracts.Tests;

/// <summary>
/// SnmMessagePackHubProtocol must produce exactly the frames the official MessagePackHubProtocol produces, for every
/// HubMessage kind, and each side must parse the other's frames.
/// </summary>
public sealed class FrameEquivalenceTests(ITestOutputHelper output)
{
    private static readonly MessagePackHubProtocol Official = new(Options.Create(new MessagePackHubProtocolOptions
    {
        SerializerOptions = MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance).WithSecurity(MessagePackSecurity.UntrustedData),
    }));

    private static readonly SnmMessagePackHubProtocol Ours = new();

    private sealed class Binder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => methodName switch
        {
            AgentHubMethods.Heartbeat => [typeof(HeartbeatDto)],
            AgentHubMethods.Register => [typeof(RegisterDto)],
            AgentHubMethods.ReportStatus => [typeof(StatusReportDto)],
            AgentHubMethods.Configure => [typeof(AgentConfigDto)],
            "Echo" => [typeof(int)],
            "Mixed" => [typeof(string), typeof(long), typeof(bool), typeof(double), typeof(byte[]), typeof(string[]), typeof(HeartbeatDto)],
            _ => throw new InvalidOperationException(methodName),
        };

        public Type GetReturnType(string invocationId) => invocationId switch
        {
            "1" => typeof(int), "2" => typeof(string), "3" => typeof(AgentConfigDto), _ => throw new InvalidOperationException(invocationId),
        };

        public Type GetStreamItemType(string streamId) => typeof(HeartbeatDto);
    }

    public static IEnumerable<object[]> Messages()
    {
        yield return ["invocation non-blocking hb", new InvocationMessage(AgentHubMethods.Heartbeat, [Samples.TypicalHeartbeat()])];
        yield return ["invocation register", new InvocationMessage("3", AgentHubMethods.Register, [Samples.TypicalRegister()])];
        yield return ["invocation status", new InvocationMessage(AgentHubMethods.ReportStatus, [Samples.TypicalStatus()])];
        yield return ["invocation configure", new InvocationMessage(AgentHubMethods.Configure, [Samples.MaxConfig()])];
        yield return ["invocation blocking", new InvocationMessage("1", "Echo", [42])];
        yield return ["invocation with headers", new InvocationMessage("1", "Echo", [-1]) { Headers = new Dictionary<string, string> { ["h"] = "v", ["x-trace"] = "abc" } }];
        yield return ["invocation with streams", new InvocationMessage("1", "Echo", [1], ["s1", "s2"])];
        yield return ["invocation mixed args", new InvocationMessage("Mixed", ["s", long.MaxValue, true, 1.5, new byte[] { 9 }, new[] { "a", "b" }, Samples.MaxHeartbeat()])];
        yield return ["invocation null dto arg", new InvocationMessage(AgentHubMethods.Heartbeat, [null])];
        yield return ["stream invocation", new StreamInvocationMessage("7", AgentHubMethods.Heartbeat, [Samples.MinHeartbeat()])];
        yield return ["stream item", new StreamItemMessage("7", Samples.EmptyDisksHeartbeat())];
        yield return ["completion void", CompletionMessage.Empty("1")];
        yield return ["completion result int", CompletionMessage.WithResult("1", 12345)];
        yield return ["completion result string", CompletionMessage.WithResult("2", "ok")];
        yield return ["completion result dto", CompletionMessage.WithResult("3", Samples.DefaultConfig())];
        yield return ["completion result null", CompletionMessage.WithResult("2", null)];
        yield return ["completion error", CompletionMessage.WithError("1", "boom")];
        yield return ["cancel invocation", new CancelInvocationMessage("7")];
        yield return ["ping", PingMessage.Instance];
        yield return ["close empty", CloseMessage.Empty];
        yield return ["close error", new CloseMessage("bye")];
        yield return ["close allowReconnect", new CloseMessage(null, allowReconnect: true)];
        yield return ["ack", new AckMessage(123456789012)];
        yield return ["sequence", new SequenceMessage(42)];
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Frames_are_byte_identical_to_official_protocol(string name, HubMessage message)
    {
        var expected = Official.GetMessageBytes(message).ToArray();
        var actual = Ours.GetMessageBytes(message).ToArray();
        output.WriteLine($"{name}: {actual.Length} bytes {Convert.ToHexString(actual)}");
        Assert.Equal(expected, actual);

        // WriteMessage (IBufferWriter path) must match GetMessageBytes.
        var buf = new ArrayBufferWriter<byte>();
        Ours.WriteMessage(message, buf);
        Assert.Equal(expected, buf.WrittenSpan.ToArray());
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Each_side_parses_the_others_frames(string name, HubMessage message)
    {
        _ = name;
        var binder = new Binder();
        var officialBytes = new ReadOnlySequence<byte>(Official.GetMessageBytes(message));
        Assert.True(Ours.TryParseMessage(ref officialBytes, binder, out var parsedByOurs));
        Assert.True(officialBytes.IsEmpty);
        var ourBytes = new ReadOnlySequence<byte>(Ours.GetMessageBytes(message));
        Assert.True(Official.TryParseMessage(ref ourBytes, binder, out var parsedByOfficial));
        Assert.True(ourBytes.IsEmpty);

        // Re-serialize both parse results with the official protocol: identical bytes => identical semantic content.
        Assert.Equal(Official.GetMessageBytes(parsedByOfficial!).ToArray(), Official.GetMessageBytes(parsedByOurs!).ToArray());
        Assert.Equal(message.GetType(), parsedByOurs!.GetType());
    }

    [Fact]
    public void Heartbeat_frame_fits_the_byte_budget()
    {
        var frame = Ours.GetMessageBytes(new InvocationMessage(AgentHubMethods.Heartbeat, [Samples.TypicalHeartbeat()]));
        output.WriteLine($"typical hb frame = {frame.Length} bytes: {Convert.ToHexString(frame.Span)}");
        Assert.True(frame.Length <= 50, $"heartbeat frame is {frame.Length} bytes, PRD budget is 50");
    }

    [Fact]
    public void Protocol_identity_matches_official()
    {
        Assert.Equal(Official.Name, Ours.Name);
        Assert.Equal(Official.Version, Ours.Version);
        Assert.Equal(Official.TransferFormat, Ours.TransferFormat);
        Assert.True(Ours.IsVersionSupported(1));
        Assert.True(Ours.IsVersionSupported(2));
        Assert.False(Ours.IsVersionSupported(3));
    }

    [Fact]
    public void Parses_concatenated_and_multi_segment_frames()
    {
        var a = Ours.GetMessageBytes(new InvocationMessage(AgentHubMethods.Heartbeat, [Samples.TypicalHeartbeat()])).ToArray();
        var b = Ours.GetMessageBytes(PingMessage.Instance).ToArray();
        var c = Ours.GetMessageBytes(new AckMessage(5)).ToArray();
        var all = a.Concat(b).Concat(c).ToArray();

        // Split into 3-byte segments to exercise multi-segment ReadOnlySequence handling.
        var seq = Segmented(all, 3);
        Assert.True(Ours.TryParseMessage(ref seq, new Binder(), out var m1));
        Assert.True(Ours.TryParseMessage(ref seq, new Binder(), out var m2));
        Assert.True(Ours.TryParseMessage(ref seq, new Binder(), out var m3));
        Assert.False(Ours.TryParseMessage(ref seq, new Binder(), out _));
        Assert.True(Samples.Equal((HeartbeatDto?)((InvocationMessage)m1!).Arguments[0], Samples.TypicalHeartbeat()));
        Assert.IsType<PingMessage>(m2);
        Assert.Equal(5, ((AckMessage)m3!).SequenceId);

        // Truncated frame: not enough data, buffer untouched.
        var truncated = new ReadOnlySequence<byte>(a, 0, a.Length - 1);
        Assert.False(Ours.TryParseMessage(ref truncated, new Binder(), out _));
        Assert.Equal(a.Length - 1, truncated.Length);
    }

    [Fact]
    public void Unknown_argument_type_throws_NotSupportedException_naming_the_type()
    {
        var ex = Assert.Throws<NotSupportedException>(() => Ours.GetMessageBytes(new InvocationMessage(AgentHubMethods.Heartbeat, [Guid.NewGuid()])));
        Assert.Contains("System.Guid", ex.Message);

        var bytes = new ReadOnlySequence<byte>(Official.GetMessageBytes(new InvocationMessage(AgentHubMethods.Heartbeat, [Samples.TypicalHeartbeat()])));
        var badBinder = new UnknownBinder();
        // Binding failures surface as InvocationBindingFailureMessage (same as the official worker), carrying our exception.
        Assert.True(Ours.TryParseMessage(ref bytes, badBinder, out var msg));
        var failure = Assert.IsType<InvocationBindingFailureMessage>(msg);
        // The vendored worker wraps binding errors in InvalidDataException (same as upstream); our NotSupportedException is the root cause.
        Exception? e = failure.BindingFailure.SourceException;
        while (e is not null && e is not NotSupportedException) e = e.InnerException;
        Assert.NotNull(e);
        Assert.Contains("System.DateTime", e!.Message);
    }

    private sealed class UnknownBinder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => [typeof(DateTime)];
        public Type GetReturnType(string invocationId) => typeof(DateTime);
        public Type GetStreamItemType(string streamId) => typeof(DateTime);
    }

    private static ReadOnlySequence<byte> Segmented(byte[] data, int segmentSize)
    {
        Segment? first = null, last = null;
        for (var i = 0; i < data.Length; i += segmentSize)
        {
            var chunk = new ReadOnlyMemory<byte>(data, i, Math.Min(segmentSize, data.Length - i));
            var seg = new Segment(chunk);
            if (first is null) first = last = seg;
            else last = last!.Append(seg);
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
            return next;
        }
    }
}
