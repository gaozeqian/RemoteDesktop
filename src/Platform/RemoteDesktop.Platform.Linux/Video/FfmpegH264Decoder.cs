using System.Runtime.Versioning;
using FFmpeg.AutoGen;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Interop;

namespace RemoteDesktop.Platform.Linux.Video;

/// <summary>
/// H.264 decoding through libavcodec, giving back NV12 frames.
/// </summary>
/// <remarks>
/// <para>
/// Every frame handed out is a fresh copy. The decoder reuses one picture internally, so returning that would mean
/// the caller's frame changing under it on the next call — and the caller is a renderer that keeps the previous
/// frame on screen until the next one is ready.
/// </para>
/// <para>
/// The decoder is asked for NV12 and its answer is checked rather than trusted. H.264 decoders normally produce
/// planar YUV, and a decoder that quietly kept doing so would hand back frames that look almost right — the colour
/// would be wrong in a way that is easy to mistake for a colour-space problem. YUV420P is converted; anything else
/// is refused with the format named.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class FfmpegH264Decoder : IVideoDecoder
{
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private bool _disposed;

    /// <param name="decoderName">The libavcodec decoder to open, normally <c>h264</c>.</param>
    /// <param name="width">The stream's expected width; used to check what comes out.</param>
    /// <param name="height">The stream's expected height.</param>
    /// <exception cref="NotSupportedException">The decoder is not in this build.</exception>
    public FfmpegH264Decoder(string decoderName, int width, int height)
    {
        FfmpegLibraries.EnsureResolved();

        ExpectedWidth = width;
        ExpectedHeight = height;
        Name = decoderName;

        var codec = ffmpeg.avcodec_find_decoder_by_name(decoderName);
        if (codec is null)
        {
            throw new NotSupportedException($"This build of FFmpeg has no '{decoderName}' decoder.");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context is null)
        {
            throw new NotSupportedException("FFmpeg would not allocate a decoder context.");
        }

        // A low-latency stream has no B-frames and arrives in order, so the decoder has no reason to hold anything
        // back; asking it not to is what keeps the picture in step with the stream.
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        var opened = ffmpeg.avcodec_open2(_context, codec, null);
        if (opened < 0)
        {
            Dispose();
            throw new NotSupportedException($"FFmpeg would not open the '{decoderName}' decoder ({Describe(opened)}).");
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_frame is null || _packet is null)
        {
            Dispose();
            throw new NotSupportedException("FFmpeg would not allocate a decoder frame or packet.");
        }
    }

    public string Name { get; }

    /// <summary>Width the caller said the stream would be; a mismatch is a protocol error rather than a decode one.</summary>
    public int ExpectedWidth { get; }

    public int ExpectedHeight { get; }

    /// <summary>
    /// Decodes one access unit.
    /// </summary>
    /// <returns>The picture, or <c>null</c> when the decoder needs more input before it can produce one.</returns>
    /// <exception cref="InvalidDataException">The bitstream cannot be decoded.</exception>
    public VideoFrame? Decode(EncodedVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Send(frame.Data.Span);

        // One access unit can produce no picture (the decoder is still filling in) or one. More than one would mean
        // the stream had more in it than the caller said, which is not something to guess about.
        var received = ffmpeg.avcodec_receive_frame(_context, _frame);
        if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
        {
            return null;
        }

        if (received < 0)
        {
            throw new InvalidDataException($"The decoder could not decode this frame ({Describe(received)}).");
        }

        try
        {
            return CopyOut(frame.Timestamp);
        }
        finally
        {
            ffmpeg.av_frame_unref(_frame);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Through locals: these take the address of the pointer to null it out, and the address of a field inside a
        // managed object cannot be taken at all.
        var frame = _frame;
        ffmpeg.av_frame_free(&frame);

        var packet = _packet;
        ffmpeg.av_packet_free(&packet);

        var context = _context;
        ffmpeg.avcodec_free_context(&context);
    }

    private void Send(ReadOnlySpan<byte> accessUnit)
    {
        // The packet borrows the caller's memory; it is only used inside this call, so nothing needs to own a copy.
        fixed (byte* data = accessUnit)
        {
            _packet->data = data;
            _packet->size = accessUnit.Length;

            var sent = ffmpeg.avcodec_send_packet(_context, _packet);
            if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                throw new InvalidDataException($"The decoder would not take this frame ({Describe(sent)}).");
            }
        }

        _packet->data = null;
        _packet->size = 0;
    }

    private MemoryVideoFrame CopyOut(TimeSpan timestamp)
    {
        var width = _frame->width;
        var height = _frame->height;
        if (width != ExpectedWidth || height != ExpectedHeight)
        {
            throw new InvalidDataException($"The stream is {width}x{height} but the session agreed on {ExpectedWidth}x{ExpectedHeight}.");
        }

        var output = new MemoryVideoFrame(width, height, PixelFormat.Nv12, timestamp);
        var target = output.Buffer;
        var y = target[..(width * height)];
        var uv = target[(width * height)..];

        var format = (AVPixelFormat)_frame->format;
        switch (format)
        {
            case AVPixelFormat.AV_PIX_FMT_NV12:
                CopyPlane(_frame->data[0], _frame->linesize[0], y, width, height);
                CopyPlane(_frame->data[1], _frame->linesize[1], uv, width, height / 2);
                break;

            case AVPixelFormat.AV_PIX_FMT_YUV420P:
                // Planar to interleaved: the luminance plane is the same, and the two chroma planes become one.
                CopyPlane(_frame->data[0], _frame->linesize[0], y, width, height);
                InterleaveChroma(_frame->data[1], _frame->data[2], _frame->linesize[1], uv, width, height / 2);
                break;

            default:
                output.Dispose();
                throw new InvalidDataException($"The decoder produced {format}, which this pipeline cannot carry.");
        }

        return output;
    }

    private static void CopyPlane(byte* source, int sourceStride, Span<byte> destination, int width, int rows)
    {
        for (var row = 0; row < rows; row++)
        {
            new ReadOnlySpan<byte>(source + (row * sourceStride), width).CopyTo(destination.Slice(row * width, width));
        }
    }

    private static void InterleaveChroma(byte* u, byte* v, int sourceStride, Span<byte> destination, int width, int rows)
    {
        for (var row = 0; row < rows; row++)
        {
            var rowStart = row * width;
            for (var column = 0; column < width; column += 2)
            {
                var source = (row * sourceStride) + column;
                destination[rowStart + column] = u[source];
                destination[rowStart + column + 1] = v[source];
            }
        }
    }

    /// <summary>Turns an FFmpeg error code into the sentence FFmpeg has for it, so logs say what went wrong.</summary>
    private static string Describe(int error)
    {
        var buffer = new byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
        fixed (byte* text = buffer)
        {
            ffmpeg.av_strerror(error, text, (ulong)buffer.Length);
            return $"{error}: {System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)text)}";
        }
    }
}
