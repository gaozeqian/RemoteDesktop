using System.Buffers.Binary;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

public class FrameCodecTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Frames_round_trip_and_clean_end_returns_null()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Frame(7, new byte[] { 1, 2, 3 }), Ct);
        await FrameCodec.WriteAsync(stream, new Frame(8, ReadOnlyMemory<byte>.Empty), Ct);
        stream.Position = 0;

        var first = await FrameCodec.ReadAsync(stream, cancellationToken: Ct);
        var second = await FrameCodec.ReadAsync(stream, cancellationToken: Ct);
        var end = await FrameCodec.ReadAsync(stream, cancellationToken: Ct);

        Assert.Equal((ushort)7, first!.Value.MessageType);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Value.Payload.ToArray());
        Assert.Equal((ushort)8, second!.Value.MessageType);
        Assert.True(second.Value.Payload.IsEmpty);
        Assert.Null(end);
    }

    [Fact]
    public async Task Oversized_length_prefix_is_rejected()
    {
        var header = new byte[FrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, uint.MaxValue);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await FrameCodec.ReadAsync(stream, maxPayloadSize: 1024, Ct));
    }

    [Fact]
    public async Task Truncated_payload_is_rejected()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Frame(1, new byte[10]), Ct);
        stream.SetLength(stream.Length - 1);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await FrameCodec.ReadAsync(stream, cancellationToken: Ct));
    }

    [Fact]
    public async Task Truncated_header_is_rejected()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0 });

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await FrameCodec.ReadAsync(stream, cancellationToken: Ct));
    }
}
