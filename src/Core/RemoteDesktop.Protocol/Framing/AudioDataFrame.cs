namespace RemoteDesktop.Protocol.Framing;

/// <summary>One Opus packet on the audio channel, sent as a raw frame (no protobuf copy).</summary>
public static class AudioDataFrame
{
    public const ushort MessageType = 3;

    public static ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> opusPacket, CancellationToken cancellationToken) =>
        FrameCodec.WriteAsync(stream, new Frame(MessageType, opusPacket), cancellationToken);
}
