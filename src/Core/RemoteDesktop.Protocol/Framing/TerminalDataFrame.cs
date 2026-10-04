namespace RemoteDesktop.Protocol.Framing;

/// <summary>Raw terminal bytes: keystrokes towards the shell, VT output from it. Sent as frames so the stream keeps its boundaries.</summary>
public static class TerminalDataFrame
{
    public const ushort MessageType = 5;

    /// <summary>Most bytes per frame; larger pastes and bursts of output are split.</summary>
    public const int MaxLength = 16 * 1024;

    public static ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        FrameCodec.WriteAsync(stream, new Frame(MessageType, data), cancellationToken);
}
