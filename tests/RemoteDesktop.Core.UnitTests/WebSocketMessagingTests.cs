using System.Net.WebSockets;
using Google.Protobuf;
using RemoteDesktop.Protocol.Control;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The signaling reader's error paths, driven by a WebSocket double that never opens a socket. These branches were
/// previously only exercised indirectly by loopback tests that always send well-formed binary envelopes.
/// </summary>
public class WebSocketMessagingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_close_frame_means_no_more_messages()
    {
        using var socket = new ScriptedWebSocket([Frame(WebSocketMessageType.Close, [], end: true)]);

        Assert.Null(await WebSocketMessaging.ReceiveAsync(socket, ControlMessage.Parser, Ct));
    }

    [Fact]
    public async Task A_text_frame_is_refused_as_non_binary()
    {
        using var socket = new ScriptedWebSocket([Frame(WebSocketMessageType.Text, [1, 2, 3], end: true)]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => WebSocketMessaging.ReceiveAsync(socket, ControlMessage.Parser, Ct));

        Assert.Contains("must be binary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_beyond_the_limit_is_refused_without_allocating_more()
    {
        var fullChunk = new byte[WebSocketMessaging.MaxMessageSize];
        using var socket = new ScriptedWebSocket([
            Frame(WebSocketMessageType.Binary, fullChunk, end: false),
            Frame(WebSocketMessageType.Binary, [1], end: true),
        ]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => WebSocketMessaging.ReceiveAsync(socket, ControlMessage.Parser, Ct));

        Assert.Contains("exceeds", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, socket.ReceiveCalls);
    }

    [Fact]
    public async Task A_fragmented_binary_message_is_reassembled()
    {
        var expected = new ControlMessage { Ping = new Ping { Timestamp = 42 } }.ToByteArray();
        using var socket = new ScriptedWebSocket([
            Frame(WebSocketMessageType.Binary, expected.AsSpan(0, 1).ToArray(), end: false),
            Frame(WebSocketMessageType.Binary, expected.AsSpan(1).ToArray(), end: true),
        ]);

        var message = await WebSocketMessaging.ReceiveAsync(socket, ControlMessage.Parser, Ct);

        Assert.NotNull(message);
        Assert.Equal(ControlMessage.BodyOneofCase.Ping, message.BodyCase);
        Assert.Equal(42L, message.Ping.Timestamp);
    }

    [Fact]
    public async Task A_malformed_envelope_is_reported_with_its_protobuf_cause()
    {
        using var socket = new ScriptedWebSocket([Frame(WebSocketMessageType.Binary, [0xFF, 0xFF, 0xFF], end: true)]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => WebSocketMessaging.ReceiveAsync(socket, ControlMessage.Parser, Ct));

        Assert.Contains("Malformed", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidProtocolBufferException>(error.InnerException);
    }

    [Fact]
    public async Task Null_socket_or_parser_is_refused()
    {
        using var socket = new ScriptedWebSocket([]);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => WebSocketMessaging.ReceiveAsync<ControlMessage>(null!, ControlMessage.Parser, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => WebSocketMessaging.ReceiveAsync<ControlMessage>(socket, null!, Ct));
    }

    private static ScriptedFrame Frame(WebSocketMessageType type, byte[] payload, bool end) => new(type, payload, end);

    private sealed record ScriptedFrame(WebSocketMessageType Type, byte[] Payload, bool EndOfMessage);

    private sealed class ScriptedWebSocket(IReadOnlyList<ScriptedFrame> frames) : WebSocket
    {
        private int _index;
        private WebSocketState _state = WebSocketState.Open;

        public int ReceiveCalls => _index;

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(_index < frames.Count, "the scripted socket ran out of frames");
            var frame = frames[_index++];
            frame.Payload.AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(new ValueWebSocketReceiveResult(frame.Payload.Length, frame.Type, frame.EndOfMessage));
        }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The product uses the Memory<byte> overload.");
    }
}
