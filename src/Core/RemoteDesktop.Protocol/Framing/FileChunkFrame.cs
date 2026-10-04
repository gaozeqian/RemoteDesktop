namespace RemoteDesktop.Protocol.Framing;

/// <summary>A piece of file content, sent as a raw frame; the position is implied by the order (starting at the agreed offset).</summary>
public static class FileChunkFrame
{
    public const ushort MessageType = 4;

    /// <summary>Content per frame. Big enough to keep the link busy, small enough for smooth progress and bounded memory.</summary>
    public const int ChunkSize = 1024 * 1024;

    public static ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken) =>
        FrameCodec.WriteAsync(stream, new Frame(MessageType, chunk), cancellationToken);
}
