using System.Buffers.Binary;
using Google.Protobuf;
using RemoteDesktop.Protocol.Control;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The edges of the frame format: what exactly is on the wire, where the payload limit sits, and what a stream that
/// delivers its bytes a few at a time (as a network does) must still produce.
/// </summary>
public class FrameCodecBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Header(uint length, ushort type)
    {
        var header = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), type);
        return header;
    }

    [Fact]
    public async Task The_header_is_length_then_type_both_big_endian()
    {
        using var stream = new MemoryStream();

        await FrameCodec.WriteAsync(stream, new Frame(0x0102, new byte[] { 0xAA, 0xBB, 0xCC }), Ct);

        Assert.Equal(FrameCodec.HeaderSize + 3, stream.Length);
        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x00, 0x03, 0x01, 0x02, 0xAA, 0xBB, 0xCC },
            stream.ToArray());
    }

    [Fact]
    public async Task The_largest_message_type_and_an_empty_payload_round_trip()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Frame(ushort.MaxValue, ReadOnlyMemory<byte>.Empty), Ct);
        stream.Position = 0;

        var frame = await FrameCodec.ReadAsync(stream, cancellationToken: Ct);

        Assert.Equal(ushort.MaxValue, frame!.Value.MessageType);
        Assert.True(frame.Value.Payload.IsEmpty);
    }

    [Fact]
    public async Task A_payload_of_exactly_the_limit_is_accepted_and_one_byte_more_is_not()
    {
        const int Limit = 1024;
        using var atLimit = new MemoryStream();
        await FrameCodec.WriteAsync(atLimit, new Frame(1, new byte[Limit]), Ct);
        atLimit.Position = 0;
        using var overLimit = new MemoryStream();
        await FrameCodec.WriteAsync(overLimit, new Frame(1, new byte[Limit + 1]), Ct);
        overLimit.Position = 0;

        var accepted = await FrameCodec.ReadAsync(atLimit, Limit, Ct);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(overLimit, Limit, Ct));

        Assert.Equal(Limit, accepted!.Value.Payload.Length);
        Assert.Contains((Limit + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payload_over_the_limit_is_refused_from_the_header_alone_without_reading_it()
    {
        // The length prefix is all that has been sent; refusing at once is what stops a hostile peer from making the
        // reader allocate, and then wait for, a buffer of the size it claims.
        using var stream = new MemoryStream(Header(length: 1_000_000, type: 1));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(stream, maxPayloadSize: 4096, Ct));

        Assert.Equal(FrameCodec.HeaderSize, stream.Position);
    }

    [Fact]
    public async Task The_default_limit_accepts_the_documented_maximum_and_refuses_beyond_it()
    {
        using var stream = new MemoryStream(Header((uint)FrameCodec.DefaultMaxPayloadSize + 1, type: 1));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(stream, cancellationToken: Ct));
        Assert.Equal(16 * 1024 * 1024, FrameCodec.DefaultMaxPayloadSize);
    }

    [Fact]
    public async Task A_limit_of_zero_accepts_only_empty_frames()
    {
        using var empty = new MemoryStream(Header(length: 0, type: 5));
        using var one = new MemoryStream([.. Header(length: 1, type: 5), 0x00]);

        var accepted = await FrameCodec.ReadAsync(empty, maxPayloadSize: 0, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(one, maxPayloadSize: 0, Ct));

        Assert.True(accepted!.Value.Payload.IsEmpty);
    }

    /// <summary>
    /// A negative limit is a caller's mistake, and the safe reading of it is "nothing is allowed". The comparison
    /// converts the limit to an unsigned number, so -1 becomes 4 294 967 295 and the check passes for every length:
    /// the guard against a hostile length prefix is switched off instead of closed. No caller passes a negative
    /// limit today (all use constants), which is why it was latent: it is refused up front now.
    /// </summary>
    [Fact]
    public async Task A_negative_limit_does_not_disable_the_check()
    {
        using var stream = new MemoryStream([.. Header(length: 1, type: 5), 0x00]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await FrameCodec.ReadAsync(stream, maxPayloadSize: -1, Ct));
    }

    /// <summary>
    /// The writer has no limit of its own - the limit belongs to the receiver, who chooses it per channel - so a frame
    /// larger than the default is written in full and it is the reading side that refuses it.
    /// </summary>
    [Fact]
    public async Task The_writer_does_not_apply_a_limit_and_the_reader_does()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Frame(1, new byte[2048]), Ct);
        stream.Position = 0;

        Assert.Equal(FrameCodec.HeaderSize + 2048, stream.Length);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(stream, maxPayloadSize: 2047, Ct));
    }

    [Fact]
    public async Task A_stream_that_hands_out_one_byte_at_a_time_still_yields_whole_frames()
    {
        using var buffer = new MemoryStream();
        await FrameCodec.WriteAsync(buffer, new Frame(3, new byte[] { 9, 8, 7, 6, 5 }), Ct);
        await FrameCodec.WriteAsync(buffer, new Frame(4, new byte[] { 1 }), Ct);
        await using var stream = new TrickleStream(buffer.ToArray());

        var first = await FrameCodec.ReadAsync(stream, cancellationToken: Ct);
        var second = await FrameCodec.ReadAsync(stream, cancellationToken: Ct);
        var end = await FrameCodec.ReadAsync(stream, cancellationToken: Ct);

        Assert.Equal((ushort)3, first!.Value.MessageType);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, first.Value.Payload.ToArray());
        Assert.Equal((ushort)4, second!.Value.MessageType);
        Assert.Equal(new byte[] { 1 }, second.Value.Payload.ToArray());
        Assert.Null(end);
    }

    [Fact]
    public async Task A_stream_that_ends_between_the_header_and_a_nonempty_payload_is_rejected()
    {
        using var stream = new MemoryStream(Header(length: 4, type: 1));

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(stream, cancellationToken: Ct));

        Assert.Contains("payload", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_header_names_the_header_in_the_error()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 1, 0 });

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameCodec.ReadAsync(stream, cancellationToken: Ct));

        Assert.Contains("header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_honours_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var stream = new MemoryStream(Header(length: 0, type: 1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await FrameCodec.ReadAsync(stream, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task Null_streams_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await FrameCodec.ReadAsync(null!, cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await FrameCodec.WriteAsync(null!, new Frame(1, ReadOnlyMemory<byte>.Empty), Ct));
    }

    /// <summary>Delivers at most one byte per read, like a link that splits everything.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length || buffer.IsEmpty)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = data[_position++];
            return ValueTask.FromResult(1);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length || count == 0)
            {
                return 0;
            }

            buffer[offset] = data[_position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Envelope framing on top of <see cref="FrameCodec"/>: whatever the peer sends, the reader answers with a message,
/// a clean end, or an <see cref="InvalidDataException"/> - never a protobuf exception the session code does not catch.
/// </summary>
public class ProtobufFramesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<MemoryStream> FrameAsync(ushort type, byte[] payload)
    {
        var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Frame(type, payload), Ct);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task An_envelope_round_trips_and_the_end_of_the_stream_is_null()
    {
        using var stream = new MemoryStream();
        await ProtobufFrames.WriteAsync(stream, new ControlMessage { Ping = new Ping { Timestamp = 42 } }, Ct);
        stream.Position = 0;

        var message = await ProtobufFrames.ReadAsync(stream, ControlMessage.Parser, cancellationToken: Ct);
        var end = await ProtobufFrames.ReadAsync(stream, ControlMessage.Parser, cancellationToken: Ct);

        Assert.Equal(ControlMessage.BodyOneofCase.Ping, message!.BodyCase);
        Assert.Equal(42L, message.Ping.Timestamp);
        Assert.Null(end);
    }

    [Fact]
    public async Task Envelopes_are_written_with_the_envelope_message_type()
    {
        using var stream = new MemoryStream();

        await ProtobufFrames.WriteAsync(stream, new ControlMessage { Ping = new Ping { Timestamp = 1 } }, Ct);

        Assert.Equal(ProtobufFrames.EnvelopeMessageType, BinaryPrimitives.ReadUInt16BigEndian(stream.ToArray().AsSpan(4)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(ushort.MaxValue)]
    public async Task A_frame_that_is_not_an_envelope_is_rejected_with_its_type_in_the_message(ushort type)
    {
        // The payload is a perfectly good message: it is the type, not the content, that disqualifies the frame.
        await using var stream = await FrameAsync(type, new ControlMessage { Ping = new Ping { Timestamp = 1 } }.ToByteArray());

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ProtobufFrames.ReadAsync(stream, ControlMessage.Parser, cancellationToken: Ct));

        Assert.Contains("Unexpected message type", error.Message, StringComparison.Ordinal);
        Assert.Contains(type.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0x0A, 0x05, 0x01 })] // a length-delimited field that claims more than there is
    [InlineData(new byte[] { 0x07 })] // field 0 with wire type 7: not a valid tag
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })] // a varint that never ends
    public async Task A_payload_that_is_not_a_protobuf_message_is_an_invalid_data_error_not_a_protobuf_one(byte[] payload)
    {
        await using var stream = await FrameAsync(ProtobufFrames.EnvelopeMessageType, payload);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ProtobufFrames.ReadAsync(stream, ControlMessage.Parser, cancellationToken: Ct));

        Assert.Contains("Malformed protobuf", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidProtocolBufferException>(error.InnerException);
    }

    [Fact]
    public async Task An_empty_payload_is_a_valid_empty_message()
    {
        await using var stream = await FrameAsync(ProtobufFrames.EnvelopeMessageType, []);

        var message = await ProtobufFrames.ReadAsync(stream, ControlMessage.Parser, cancellationToken: Ct);

        Assert.Equal(ControlMessage.BodyOneofCase.None, message!.BodyCase);
    }

    [Fact]
    public async Task The_payload_limit_applies_to_envelopes_too()
    {
        var message = new ControlMessage { Ping = new Ping { Timestamp = long.MaxValue } };
        var size = message.CalculateSize();
        using var written = new MemoryStream();
        await ProtobufFrames.WriteAsync(written, message, Ct);

        written.Position = 0;
        var atLimit = await ProtobufFrames.ReadAsync(written, ControlMessage.Parser, size, Ct);
        written.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ProtobufFrames.ReadAsync(written, ControlMessage.Parser, size - 1, Ct));

        Assert.Equal(long.MaxValue, atLimit!.Ping.Timestamp);
    }

    [Fact]
    public async Task A_null_parser_or_message_is_refused()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ProtobufFrames.ReadAsync<ControlMessage>(stream, null!, cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await ProtobufFrames.WriteAsync(stream, null!, Ct));
    }
}
