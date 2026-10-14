using System.Runtime.Versioning;
using FFmpeg.AutoGen;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Interop;

namespace RemoteDesktop.Platform.Linux.Video;

/// <summary>
/// H.264 encoding through libavcodec, low-latency and constant-bitrate.
/// </summary>
/// <remarks>
/// <para>
/// One type covers both the software and the hardware encoders, because through libavcodec they differ only in which
/// encoder is named and whether a frame has to be uploaded to a device first. Everything the host depends on — Annex-B
/// output, no B-frames, key frames only when asked for, a bitrate that can be moved mid-stream — is set the same way
/// for all of them, and the settings follow the Media Foundation encoder's on Windows so that the same picture comes
/// out of both platforms.
/// </para>
/// <para>
/// The encoder is configured for a screen rather than for a file: the picture changes only where the user changed
/// something, so the bitrate goes into quality instead of into motion, and a long GOP means the only key frames are
/// the ones the controller explicitly asks for after a loss.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class FfmpegH264Encoder : IVideoEncoder
{
    // Not readonly: the free functions take the address of these and write null back into them.
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private readonly string _name;
    private long _frameIndex;
    private bool _disposed;

    /// <param name="encoderName">The libavcodec encoder to open, e.g. <c>libx264</c> or <c>h264_vaapi</c>.</param>
    /// <param name="options">Frame size, rate and starting bitrate.</param>
    /// <param name="hardware">
    /// Whether this encoder runs on a device. Reported to the controller, and used by the host to decide whether a
    /// failure should fall back to software — so it has to say what is actually true, not what was hoped for.
    /// </param>
    /// <param name="device">The render node for a hardware encoder; ignored by the software one.</param>
    /// <param name="uploadToDevice">
    /// Whether frames have to be copied onto a device before encoding. True for VA-API, false for NVENC, which takes
    /// the frame as it is — which is why this is separate from <paramref name="hardware"/>.
    /// </param>
    /// <exception cref="NotSupportedException">The encoder is not in this build, or refused the settings.</exception>
    public FfmpegH264Encoder(
        string encoderName,
        VideoEncoderOptions options,
        bool hardware = false,
        string? device = null,
        bool uploadToDevice = false)
    {
        FfmpegLibraries.EnsureResolved();
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        IsHardware = hardware;
        _name = encoderName;

        var codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        if (codec is null)
        {
            throw new NotSupportedException($"This build of FFmpeg has no '{encoderName}' encoder.");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        if (_context is null)
        {
            throw new NotSupportedException("FFmpeg would not allocate an encoder context.");
        }

        try
        {
            Configure(codec, options, device, uploadToDevice);

            Open(codec, encoderName);
        }
        catch
        {
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            throw;
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_frame is null || _packet is null)
        {
            Dispose();
            throw new NotSupportedException("FFmpeg would not allocate an encoder frame or packet.");
        }

        // The frame has to be told its shape before a buffer is asked for; a frame with no size is a request FFmpeg
        // refuses rather than guesses at.
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        _frame->width = options.Width;
        _frame->height = options.Height;

        var allocated = ffmpeg.av_frame_get_buffer(_frame, align: 0);
        if (allocated < 0)
        {
            Dispose();
            throw new NotSupportedException($"FFmpeg would not allocate an encoder frame ({Describe(allocated)}).");
        }

        // The first frame is always a key frame: a controller that joins mid-stream has nothing to start from
        // otherwise, and the host asks for one at the start of every session for the same reason.
        _frame->pts = 0;
    }

    private AVBufferRef* _deviceRef;
    private AVBufferRef* _framesRef;

    public string Name => _name;

    public bool IsHardware { get; }

    public VideoEncoderOptions Options { get; }

    /// <summary>
    /// How many B-frames the encoder is set to use, read back from it rather than remembered from what was asked for.
    /// Exposed so that a test can check the setting actually took: it is the difference between a picture that keeps
    /// up and one that is always a few frames behind.
    /// </summary>
    public int MaxBFrames => _context->max_b_frames;

    /// <summary>Encodes one NV12 frame and returns every access unit that became ready.</summary>
    /// <exception cref="InvalidDataException">The frame is not the size and format this encoder was opened for.</exception>
    public IReadOnlyList<EncodedVideoFrame> Encode(VideoFrame frame, bool forceKeyFrame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frame.Format != PixelFormat.Nv12 || frame.Width != Options.Width || frame.Height != Options.Height)
        {
            throw new InvalidDataException(
                $"This encoder takes NV12 at {Options.Width}x{Options.Height}; got {frame.Format} at {frame.Width}x{frame.Height}.");
        }

        var pixels = frame.GetPixels(out var stride);
        if (stride != frame.Width)
        {
            throw new InvalidDataException($"NV12 input must be tightly packed; the stride is {stride} for a width of {frame.Width}.");
        }

        var timestamp = frame.Timestamp;
        CopyIntoEncoderFrame(pixels);

        // A key frame is requested, never periodic: the controller asks after a loss, and the host asks when it
        // creates the encoder. The GOP is long so that nothing else produces one.
        _frame->pict_type = forceKeyFrame ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        if (forceKeyFrame)
        {
            _frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
        }

        _frame->pts = _frameIndex++;
        Send();

        return Collect(timestamp);
    }

    /// <summary>
    /// Moves the target bitrate. Takes effect from the next frame rather than needing the encoder to be rebuilt.
    /// </summary>
    /// <remarks>
    /// The host adjusts the bitrate as often as every half second when a link is congested, so an implementation that
    /// only took a new bitrate at construction would turn a slow link into a stalled one.
    /// </remarks>
    public void SetBitrate(int bitrateKbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bitsPerSecond = bitrateKbps * 1000L;
        _context->bit_rate = bitsPerSecond;

        // Both bounds move together: with maxrate left behind, a lowered target is ignored the moment the encoder
        // needs the headroom.
        SetOption("maxrate", bitsPerSecond);
        SetOption("bufsize", bitsPerSecond);
        SetRcBufferSize(bitsPerSecond);
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

        var frames = _framesRef;
        if (frames is not null)
        {
            ffmpeg.av_buffer_unref(&frames);
        }

        var device = _deviceRef;
        if (device is not null)
        {
            ffmpeg.av_buffer_unref(&device);
        }
    }

    /// <summary>The rate-control buffer, which on some encoders is a private option rather than a field.</summary>
    private void SetRcBufferSize(long bitsPerSecond)
    {
        SetOption("bufsize", bitsPerSecond);
    }

    private void SetOption(string name, long value) =>
        ffmpeg.av_opt_set(_context->priv_data, name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);

    private void SetOption(string name, string value)
    {
        if (_context->priv_data is not null)
        {
            ffmpeg.av_opt_set(_context->priv_data, name, value, 0);
        }
    }

    private void Configure(AVCodec* codec, VideoEncoderOptions options, string? device, bool uploadToDevice)
    {
        _context->width = options.Width;
        _context->height = options.Height;
        _context->time_base = new AVRational { num = 1, den = options.FrameRate };
        _context->framerate = new AVRational { num = options.FrameRate, den = 1 };

        var bitsPerSecond = options.BitrateKbps * 1000L;
        _context->bit_rate = bitsPerSecond;

        // No B-frames: they reorder output, which means holding frames back, which is the one thing a remote desktop
        // cannot afford.
        _context->max_b_frames = 0;

        // A long GOP, so the only key frames are the ones that were asked for.
        _context->gop_size = Math.Max(1, options.FrameRate) * 10;

        // Output stays Annex-B, which is what the wire format and H264Bitstream expect. Setting GLOBAL_HEADER would
        // move SPS/PPS out of the stream and into extradata, where the controller would never see them.
        _context->flags &= ~ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (uploadToDevice)
        {
            var deviceContext = (AVBufferRef*)null;
            var created = ffmpeg.av_hwdevice_ctx_create(&deviceContext, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, device, null, 0);
            if (created < 0)
            {
                throw new NotSupportedException($"Could not open the encoding device '{device}' ({Describe(created)}).");
            }

            _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
            var framesContext = ffmpeg.av_hwframe_ctx_alloc(deviceContext);
            if (framesContext is null)
            {
                throw new NotSupportedException("FFmpeg would not allocate a hardware frame pool.");
            }

            var frames = (AVHWFramesContext*)framesContext->data;
            frames->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
            frames->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            frames->width = options.Width;
            frames->height = options.Height;
            frames->initial_pool_size = 8;

            var initialised = ffmpeg.av_hwframe_ctx_init(framesContext);
            if (initialised < 0)
            {
                throw new NotSupportedException($"Could not set up the encoding device ({Describe(initialised)}).");
            }

            _context->hw_frames_ctx = ffmpeg.av_buffer_ref(framesContext);
            _deviceRef = deviceContext;
            _framesRef = framesContext;
        }
        else
        {
            _context->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        }

        _context->profile = ffmpeg.AV_PROFILE_H264_MAIN;

        // Presets are the encoder's own options, so these only apply where the encoder knows the name. A hardware
        // encoder without them ignores the calls and is configured by the fields above.
        SetOption("preset", options.Preference == EncoderPreference.Quality ? "medium" : "veryfast");
        SetOption("tune", "zerolatency");
        SetOption("rc_mode", "CBR");

        // Constant bitrate, so that a burst on screen cannot push the link past what it can carry.
        SetOption("maxrate", bitsPerSecond);
        SetOption("bufsize", bitsPerSecond);
    }

    private void Open(AVCodec* codec, string encoderName)
    {
        var opened = ffmpeg.avcodec_open2(_context, codec, null);
        if (opened < 0)
        {
            throw new NotSupportedException(
                $"'{encoderName}' refused {Options.Width}x{Options.Height} at {Options.BitrateKbps} kbps ({Describe(opened)}).");
        }
    }

    private void CopyIntoEncoderFrame(ReadOnlySpan<byte> pixels)
    {
        var width = Options.Width;
        var height = Options.Height;

        // NV12 is a Y plane and then an interleaved UV plane, each tightly packed in the source.
        var y = pixels[..(width * height)];
        var uv = pixels[(width * height)..];

        Copy(y, _frame->data[0], _frame->linesize[0], width, height);
        Copy(uv, _frame->data[1], _frame->linesize[1], width, height / 2);
    }

    private static void Copy(ReadOnlySpan<byte> source, byte* destination, int destinationStride, int width, int rows)
    {
        for (var row = 0; row < rows; row++)
        {
            source.Slice(row * width, width).CopyTo(new Span<byte>(destination + (row * destinationStride), width));
        }
    }

    private void Send()
    {
        var sent = ffmpeg.avcodec_send_frame(_context, _frame);

        // EAGAIN means the encoder is still holding earlier frames; what it has ready comes out on the receive below.
        if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            throw new InvalidDataException($"The encoder would not take a frame ({Describe(sent)}).");
        }
    }

    private List<EncodedVideoFrame> Collect(TimeSpan timestamp)
    {
        var frames = new List<EncodedVideoFrame>();

        // Zero or more: none while the encoder is still buffering, occasionally more than one when a late access unit
        // arrives alongside the current one. All of them have to be sent.
        while (true)
        {
            var received = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (received < 0)
            {
                throw new InvalidDataException($"The encoder produced nothing usable ({Describe(received)}).");
            }

            var data = new ReadOnlySpan<byte>(_packet->data, _packet->size).ToArray();
            frames.Add(new EncodedVideoFrame(data, (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0, timestamp));

            ffmpeg.av_packet_unref(_packet);
        }

        return frames;
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
