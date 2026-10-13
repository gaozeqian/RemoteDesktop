using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Video;

/// <summary>
/// H.264 decoding through a VideoToolbox decompression session, delivering NV12 pictures cropped to the size the
/// stream asked for.
/// </summary>
/// <remarks>
/// <para>
/// The session is created lazily by the helper, on the first access unit that carries parameter sets — the
/// constructor wants a format description, and a stream that has not begun has none. Until then
/// <see cref="Decode"/> reports <see cref="InvalidDataException"/>, which is what makes "the stream started
/// mid-picture" a diagnosable case rather than a decoder that silently fails on every later frame.
/// </para>
/// <para>
/// The stream size is learned from the first picture, not from the <see cref="VideoToolboxH264Decoder"/>
/// constructor: the wire format does not carry dimensions ahead of the parameter sets that imply them.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class VideoToolboxH264Decoder : IVideoDecoder
{
    private readonly MacVideoFrameSink _sink = new();
    private readonly byte[] _scratch = new byte[512];
    private nint _decoder;
    private bool _disposed;

    public string Name => "VideoToolbox H.264 decoder";

    public VideoFrame? Decode(EncodedVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var data = frame.Data;
        if (data.Length == 0)
        {
            return null;
        }

        var picture = default(RdVideoPicture);
        var status = 0;

        // Pinning the memory rather than fixing a span: the bitstream arrives as a ReadOnlyMemory, and a fixed
        // statement over its Span is not allowed. The pin has to outlive the call, so it is scoped outside the
        // unsafe block that uses it.
        using var pinned = data.Pin();
        unsafe
        {
            RdVideoPicture* picturePointer = &picture;
            byte* annexB = (byte*)pinned.Pointer;
            fixed (byte* error = _scratch)
            {
                error[0] = 0;
                status = MacNativeInterop.rd_video_decoder_decode(
                    _decoder,
                    annexB,
                    (nuint)data.Length,
                    (long)frame.Timestamp.Ticks * 100,
                    2000,
                    picturePointer,
                    error,
                    (nuint)_scratch.Length);
            }
        }

        if (status == MacNativeInterop.Ok)
        {
            // No picture is not a failure: a parameter-set-only unit, or a decoder still filling its pipeline.
            return picture.HasFrame == 0 ? null : CopyPicture(picture, frame.Timestamp);
        }

        var detail = ReadError(_scratch);
        throw new InvalidDataException(
            detail.Length > 0 ? detail : $"VideoToolbox decoding failed with status {status}.");
    }

    /// <summary>Opens the session. The helper defers the real work until the first key frame arrives.</summary>
    internal void Open()
    {
        if (_decoder != 0)
        {
            throw new InvalidOperationException("The decoder was already opened.");
        }

        var status = 0;
        unsafe
        {
            fixed (byte* error = _scratch)
            {
                error[0] = 0;
                status = MacNativeInterop.rd_video_decoder_create(
                    VideoCodecFourCc.H264,
                    MacNativeInterop.VideoDecoderCallback,
                    _sink.RefCon,
                    out _decoder,
                    error,
                    (nuint)_scratch.Length);
            }
        }

        if (status != MacNativeInterop.Ok)
        {
            var detail = ReadError(_scratch);
            throw new InvalidOperationException(
                detail.Length > 0 ? detail : $"No VideoToolbox decoder could be created (status {status}).");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_decoder != 0)
        {
            MacNativeInterop.rd_video_decoder_destroy(_decoder);
            _decoder = 0;
        }

        _sink.Dispose();
    }

    /// <summary>
    /// Copies the two planes out of the helper's buffers into a managed frame.
    /// </summary>
    /// <remarks>
    /// The planes are already tightly packed by the helper, so this is one memcpy each rather than the row-by-row
    /// walk the Media Foundation decoder needs — its stride comes from a media type, whereas here the helper has
    /// already done the cropping.
    /// </remarks>
    private static MemoryVideoFrame CopyPicture(RdVideoPicture picture, TimeSpan timestamp)
    {
        var width = picture.Width;
        var height = picture.Height;
        if (width <= 0 || height <= 0 || (width % 2) != 0 || (height % 2) != 0)
        {
            throw new InvalidDataException("The decoded picture has dimensions NV12 cannot hold.");
        }

        var frame = new MemoryVideoFrame(width, height, PixelFormat.Nv12, timestamp);

        var destination = frame.Buffer;
        unsafe
        {
            // The Y plane, then the interleaved UV plane at half the height.
            new ReadOnlySpan<byte>(picture.Y, width * height).CopyTo(destination);
            new ReadOnlySpan<byte>(picture.Uv, width * (height / 2))
                .CopyTo(destination.Slice(width * height));
        }

        return frame;
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
}
