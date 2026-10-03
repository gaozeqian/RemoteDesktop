using System.Buffers.Binary;
using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Protocol.Framing;

/// <summary>
/// An encoded picture on the video channel, sent as a raw frame (no protobuf copy):
/// <c>[flags u8][capture time unix-µs i64][Annex-B access unit]</c>.
/// </summary>
/// <remarks>
/// The capture time is wall-clock so the controller can estimate end-to-end latency; it is only meaningful when
/// both clocks are synchronized (same machine, or NTP), and is never used for anything but statistics.
/// </remarks>
public static class VideoDataFrame
{
    public const ushort MessageType = 2;

    private const int HeaderSize = 1 + sizeof(long);
    private const byte KeyFrameFlag = 1;

    public static async ValueTask WriteAsync(Stream stream, EncodedVideoFrame frame, DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var payload = new byte[HeaderSize + frame.Data.Length];
        payload[0] = frame.IsKeyFrame ? KeyFrameFlag : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), ToUnixMicroseconds(capturedAt));
        frame.Data.Span.CopyTo(payload.AsSpan(HeaderSize));
        await FrameCodec.WriteAsync(stream, new Frame(MessageType, payload), cancellationToken).ConfigureAwait(false);
    }

    /// <exception cref="InvalidDataException">The frame is too short.</exception>
    public static (EncodedVideoFrame Frame, DateTimeOffset CapturedAt) Parse(Frame frame)
    {
        var payload = frame.Payload;
        if (payload.Length < HeaderSize)
        {
            throw new InvalidDataException("Video frame too short.");
        }

        var span = payload.Span;
        var capturedAt = FromUnixMicroseconds(BinaryPrimitives.ReadInt64BigEndian(span[1..]));
        var encoded = new EncodedVideoFrame(payload[HeaderSize..], (span[0] & KeyFrameFlag) != 0, TimeSpan.Zero);
        return (encoded, capturedAt);
    }

    private static long ToUnixMicroseconds(DateTimeOffset time) => (time.UtcTicks - DateTime.UnixEpoch.Ticks) / 10;

    private static DateTimeOffset FromUnixMicroseconds(long value) => new(DateTime.UnixEpoch.Ticks + (value * 10), TimeSpan.Zero);
}
