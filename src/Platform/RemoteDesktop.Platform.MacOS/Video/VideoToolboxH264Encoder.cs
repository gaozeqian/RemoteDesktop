using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Shared;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Video;

/// <summary>
/// H.264 encoding through a VideoToolbox compression session, configured for low latency: real-time, no frame
/// reordering (so no B-frames), one picture out per picture in.
/// </summary>
/// <remarks>
/// Not thread-safe; the video pipeline drives it from a single loop, as it does every other encoder.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class VideoToolboxH264Encoder : IVideoEncoder
{
    private readonly MacVideoFrameSink _sink = new();
    private readonly TimeSpan _outputWait;
    private readonly byte[] _scratch = new byte[512];
    private nint _encoder;
    private bool _disposed;

    public VideoToolboxH264Encoder(bool preferSoftware, VideoEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Name = DescribeName(preferSoftware);
        IsHardware = !preferSoftware;
        Options = options;
        // The MF encoder waits max(20ms, 2000/fps) for its output. The same bound here: a frame the encoder has
        // not produced by then is one it is buffering, and waiting longer would only stall the caller.
        _outputWait = TimeSpan.FromMilliseconds(Math.Max(20, 2000.0 / Math.Max(1, options.FrameRate)));
    }

    /// <summary>What this encoder is, as the port contract's diagnostics string.</summary>
    /// <remarks>
    /// Private-set because the helper reports which encoder actually took the configuration, and the choice is a
    /// lookup that can fall back to the platform default.
    /// </remarks>
    public string Name { get; private set; } = string.Empty;

    public bool IsHardware { get; }

    public VideoEncoderOptions Options { get; }

    public IReadOnlyList<EncodedVideoFrame> Encode(VideoFrame frame, bool forceKeyFrame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        EncoderInput.Validate(frame, Options, message => new ArgumentException(message, nameof(frame)));

        var pixels = frame.GetPixels(out var stride);
        if (stride != frame.Width)
        {
            // The native layer takes a tightly packed buffer: it hands VideoToolbox one plane per plane pointer,
            // and a padded stride would shift every row.
            throw new ArgumentException("NV12 input must be tightly packed.", nameof(frame));
        }

        var size = (nuint)((long)frame.Width * frame.Height * 3 / 2);
        var packets = new RdVideoPacket[4];
        var timestamp = (long)frame.Timestamp.Ticks * 100;

        var count = 0;
        var status = 0;
        unsafe
        {
            fixed (byte* nv12 = pixels)
            fixed (RdVideoPacket* packetsPointer = packets)
            fixed (byte* error = _scratch)
            {
                error[0] = 0;
                status = MacNativeInterop.rd_video_encoder_encode(
                    _encoder,
                    nv12,
                    (nuint)size,
                    forceKeyFrame ? 1 : 0,
                    timestamp,
                    packetsPointer,
                    packets.Length,
                    out count,
                    (int)Math.Ceiling(_outputWait.TotalMilliseconds),
                    error,
                    (nuint)_scratch.Length);
            }
        }

        if (status != MacNativeInterop.Ok)
        {
            throw new InvalidOperationException(DescribeFailure(status, ReadError(_scratch)));
        }

        var results = new EncodedVideoFrame[count];
        for (var i = 0; i < count; i++)
        {
            // A copy, not a reference: the helper's buffer is reused by the next encode call.
            var data = new ReadOnlyMemory<byte>(CopyOf(packets[i]));
            results[i] = new EncodedVideoFrame(data, packets[i].IsKeyFrame != 0, frame.Timestamp);
        }

        return results;
    }

    public void SetBitrate(int bitrateKbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (MacNativeInterop.rd_video_encoder_set_bitrate(_encoder, bitrateKbps) != MacNativeInterop.Ok)
        {
            throw new InvalidOperationException($"The encoder would not change to {bitrateKbps} kbit/s.");
        }
    }

    /// <summary>Opens the session. Separate from the constructor so a failure is a creation failure, not a bad object.</summary>
    internal void Open()
    {
        if (_encoder != 0)
        {
            throw new InvalidOperationException("The encoder was already opened.");
        }

        var name = new byte[256];
        var count = 0;
        var status = 0;
        unsafe
        {
            fixed (byte* namePointer = name)
            fixed (byte* error = _scratch)
            {
                error[0] = 0;
                status = MacNativeInterop.rd_video_encoder_create(
                    VideoCodecFourCc.H264,
                    Options.Width,
                    Options.Height,
                    Options.FrameRate,
                    Options.BitrateKbps,
                    IsHardware ? 0 : 1,
                    MacNativeInterop.VideoEncoderCallback,
                    _sink.RefCon,
                    out _encoder,
                    namePointer,
                    (nuint)name.Length,
                    out var hardware,
                    error,
                    (nuint)_scratch.Length);

                count = hardware;
            }
        }

        if (status != MacNativeInterop.Ok)
        {
            var detail = ReadError(_scratch);
            throw new InvalidOperationException(
                detail.Length > 0 ? detail : $"No VideoToolbox encoder accepted {Options.Width}x{Options.Height}.");
        }

        // The helper reports which encoder actually accepted the configuration, because the choice is a lookup
        // that can fall back to the platform default. Reporting the wrong backend would send a reader hunting in
        // the wrong place after a codec failure. IsHardware is fixed at construction and deliberately not updated:
        // the fallback it would report is a transient state of one session, not a property of this encoder.
        var reported = ReadName(name);
        if (reported.Length > 0)
        {
            Name = reported;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_encoder != 0)
        {
            MacNativeInterop.rd_video_encoder_destroy(_encoder);
            _encoder = 0;
        }

        _sink.Dispose();
    }

    private static string DescribeFailure(int status, string? error) =>
        error is { Length: > 0 }
            ? error
            : $"VideoToolbox encoding failed with status {status}.";

    private static byte[] CopyOf(RdVideoPacket packet)
    {
        var data = new byte[packet.Length];
        if (packet.Length > 0)
        {
            unsafe
            {
                new ReadOnlySpan<byte>(packet.Data, (int)packet.Length).CopyTo(data);
            }
        }

        return data;
    }

    private static string ReadError(byte[] buffer)
    {
        var end = 0;
        while (end < buffer.Length && buffer[end] != 0)
        {
            end++;
        }

        return end == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(buffer, 0, end);
    }

    private static string ReadName(byte[] buffer) => ReadError(buffer);

    private static string DescribeName(bool preferSoftware) =>
        preferSoftware ? "VideoToolbox software H.264" : "VideoToolbox hardware H.264";
}

/// <summary>Four-character codecs as the integers the helper takes them.</summary>
internal static class VideoCodecFourCc
{
    /// <summary>'avc1' — H.264, the codec the wire carries.</summary>
    public const int H264 = 0x61766331;
}
