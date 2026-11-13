using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Interop;

namespace RemoteDesktop.Platform.Linux.Video;

/// <summary>
/// Records frames into an MP4 file, through libavcodec and libavformat.
/// </summary>
/// <remarks>
/// <para>
/// The work happens on its own thread behind a short queue, the same shape the Windows recorder has. Writing a
/// recording is not what the caller is for: it is driven by a UI that is also drawing the session, and encoding a
/// frame inline would make the recording the reason the picture stutters.
/// </para>
/// <para>
/// The queue is a second long and drops its oldest entry when it fills. A caller that cannot keep up — a slow disk,
/// a busy machine — gets a recording that skips rather than a queue that grows until the machine runs out of memory.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class FfmpegVideoRecorder : IVideoRecorder
{
    /// <summary>A second of frames at the usual rate. Beyond that the recording is behind live and dropping is kind.</summary>
    private const int QueueLength = 30;

    private readonly Lock _gate = new();

    private Channel<RecordSample>? _channel;
    private Task? _worker;
    private unsafe AVFormatContext* _format;
    private unsafe AVCodecContext* _encoder;
    private unsafe AVStream* _stream;
    private unsafe SwsContext* _scaler;
    private unsafe AVFrame* _frame;
    private unsafe AVPacket* _packet;
    private unsafe byte*[]? _destinationData;
    private int[]? _destinationStride;
    private long _written;
    private int _width;
    private int _height;
    private int _failure;
    private DateTime _started;
    private string? _outputPath;

    public bool IsRecording => _channel is not null;

    public string? OutputFilePath => _outputPath;

    public TimeSpan Duration => _channel is null ? TimeSpan.Zero : DateTime.UtcNow - _started;

    public long WrittenFrames => Interlocked.Read(ref _written);

    public VideoRecorderFailure Failure => (VideoRecorderFailure)Volatile.Read(ref _failure);

    public void Start(string filePath, int width, int height, int frameRate = 30, int bitrateKbps = 4000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);

        lock (_gate)
        {
            if (_channel is not null)
            {
                throw new InvalidOperationException("A recording is already running.");
            }

            FfmpegLibraries.EnsureResolved();

            // The reason the recording is not being written is the first one, and stays that way: a later frame that
            // also fails says nothing about where the file actually ends.
            Volatile.Write(ref _failure, (int)VideoRecorderFailure.None);
            Interlocked.Exchange(ref _written, 0);
            _outputPath = filePath;
            _started = DateTime.UtcNow;

            // Kept as plain numbers: RecordFrame runs on the caller's thread and must not read the native encoder
            // context, which Close frees when a stop on another thread finishes.
            _width = width;
            _height = height;

            Open(filePath, width, height, frameRate, bitrateKbps);

            var channel = Channel.CreateBounded<RecordSample>(new BoundedChannelOptions(QueueLength)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

            _channel = channel;
            _worker = Task.Run(() => ProcessQueueAsync(channel));
        }
    }

    public void RecordFrame(ReadOnlySpan<byte> bgraPixels, int stride, int width, int height)
    {
        var channel = _channel;
        if (channel is null)
        {
            return;
        }

        // A picture that changes size cannot be recorded into a file whose frames are one size; the Windows recorder
        // stops for the same reason. The frames already written stay valid.
        if (width != _width || height != _height)
        {
            Fail(VideoRecorderFailure.PictureSizeChanged);
            return;
        }

        // The last row needs only its visible bytes, not the padding after it, which is all the other recorders ask for.
        var required = ((height - 1) * stride) + (width * 4);
        if (stride < width * 4 || bgraPixels.Length < required)
        {
            Fail(VideoRecorderFailure.WriteFailed);
            return;
        }

        // Copied here rather than on the worker: the caller owns the pixels only until it returns. The buffer keeps
        // the full stride * height size so the converter's view of the last row is the same as with a padded frame.
        var pixels = new byte[stride * height];
        bgraPixels[..required].CopyTo(pixels);
        channel.Writer.TryWrite(new RecordSample(pixels, stride, width, height));
    }

    public async Task<string> StopAsync()
    {
        Channel<RecordSample>? channel;
        Task? worker;

        lock (_gate)
        {
            channel = _channel;
            worker = _worker;
            _channel = null;
            _worker = null;
        }

        if (channel is null || worker is null)
        {
            // Nothing is running (never started, or already stopped): same answer as the Windows and macOS recorders
            // give, the path of the latest recording, or an empty string when there has been none.
            return _outputPath ?? string.Empty;
        }

        channel.Writer.TryComplete();
        await worker.ConfigureAwait(false);

        var path = _outputPath!;

        // Nothing reached the encoder: there is no picture to finalize, and a file holding no frames is removed
        // rather than left behind as something a person would try to play.
        if (Interlocked.Read(ref _written) == 0)
        {
            Fail(VideoRecorderFailure.NothingRecorded);
            Close(keepFile: false);
            throw new InvalidOperationException("No frame was recorded, so there is nothing to finalize.");
        }

        Close(keepFile: true);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Nothing was recorded; StopAsync has already cleaned up.
            }
        }
    }

    /// <summary>Records the first reason and keeps it.</summary>
    private void Fail(VideoRecorderFailure failure) =>
        Interlocked.CompareExchange(ref _failure, (int)failure, (int)VideoRecorderFailure.None);

    private unsafe void Open(string filePath, int width, int height, int frameRate, int bitrateKbps)
    {
        AVFormatContext* format = null;
        var opened = ffmpeg.avformat_alloc_output_context2(&format, null, "mp4", filePath);
        if (opened < 0 || format is null)
        {
            throw new InvalidOperationException($"Could not prepare '{filePath}' for recording ({Describe(opened)}).");
        }

        _format = format;

        try
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name("libx264");
            if (codec is null)
            {
                throw new NotSupportedException("This build of FFmpeg has no H.264 encoder to record with.");
            }

            _encoder = ffmpeg.avcodec_alloc_context3(codec);
            if (_encoder is null)
            {
                throw new InvalidOperationException("FFmpeg would not allocate a recording encoder.");
            }

            _encoder->width = width;
            _encoder->height = height;
            _encoder->time_base = new AVRational { num = 1, den = frameRate };

            // Recording writes to a file that will be played back, not to a link with a person waiting at the other
            // end, so this one may use B-frames and a normal GOP: both make the file smaller for the same quality.
            _encoder->max_b_frames = 2;
            _encoder->gop_size = frameRate * 2;
            _encoder->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            _encoder->bit_rate = bitrateKbps * 1000L;

            // The mp4 container wants its parameter sets in the stream header rather than in front of the first
            // picture, which is the opposite of what the streaming encoder needs.
            if ((_format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                _encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            var started = ffmpeg.avcodec_open2(_encoder, codec, null);
            if (started < 0)
            {
                throw new InvalidOperationException($"The recording encoder refused {width}x{height} ({Describe(started)}).");
            }

            _stream = ffmpeg.avformat_new_stream(_format, null);
            if (_stream is null)
            {
                throw new InvalidOperationException("FFmpeg would not add a stream to the recording.");
            }

            _stream->time_base = _encoder->time_base;
            _ = ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _encoder);

            if ((_format->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                var openedOutput = ffmpeg.avio_open(&_format->pb, filePath, ffmpeg.AVIO_FLAG_WRITE);
                if (openedOutput < 0)
                {
                    throw new InvalidOperationException($"Could not open '{filePath}' for writing ({Describe(openedOutput)}).");
                }
            }

            var header = ffmpeg.avformat_write_header(_format, null);
            if (header < 0)
            {
                throw new InvalidOperationException($"Could not write the recording's header ({Describe(header)}).");
            }

            // The frames arrive as BGRA, which is what the capture and the display already work in. Converting once,
            // here, keeps the rest of the pipeline from having to know what a recording wants.
            _scaler = ffmpeg.sws_getContext(
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);

            if (_scaler is null)
            {
                throw new InvalidOperationException("FFmpeg would not set up the colour conversion for the recording.");
            }

            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            if (_frame is null || _packet is null)
            {
                throw new InvalidOperationException("FFmpeg would not allocate a recording frame or packet.");
            }

            _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            _frame->width = width;
            _frame->height = height;

            if (ffmpeg.av_frame_get_buffer(_frame, 32) < 0)
            {
                throw new InvalidOperationException("FFmpeg would not allocate the recording's frame buffer.");
            }

            // sws_scale is bound as taking arrays rather than the bare pointer the frame holds, so the plane
            // addresses are gathered once. The frame's own buffers are allocated once too, so they do not move.
            _destinationData =
            [
                _frame->data[0], _frame->data[1], _frame->data[2], _frame->data[3],
                _frame->data[4], _frame->data[5], _frame->data[6], _frame->data[7],
            ];

            _destinationStride =
            [
                _frame->linesize[0], _frame->linesize[1], _frame->linesize[2], _frame->linesize[3],
                _frame->linesize[4], _frame->linesize[5], _frame->linesize[6], _frame->linesize[7],
            ];
        }
        catch
        {
            Close(keepFile: false);
            throw;
        }
    }

    private async Task ProcessQueueAsync(Channel<RecordSample> channel)
    {
        try
        {
            await foreach (var sample in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    Write(sample);
                }
#pragma warning disable CA1031 // A failed recording must not end the session that is being recorded.
                catch (Exception)
#pragma warning restore CA1031
                {
                    Fail(VideoRecorderFailure.WriteFailed);
                }
            }
        }
#pragma warning disable CA1031 // Same: the worker ending must not take the process with it.
        catch (Exception)
#pragma warning restore CA1031
        {
            Fail(VideoRecorderFailure.WriteFailed);
        }
    }

    private unsafe void Write(RecordSample sample)
    {
        if (_frame->data[0] is null)
        {
            return;
        }

        if (ffmpeg.av_frame_make_writable(_frame) < 0)
        {
            Fail(VideoRecorderFailure.WriteFailed);
            return;
        }

        fixed (byte* source = sample.Pixels)
        {
            byte*[] sourceData = [source, null, null, null, null, null, null, null];
            int[] sourceStride = [sample.Stride, 0, 0, 0, 0, 0, 0, 0];

            var converted = ffmpeg.sws_scale(
                _scaler, sourceData, sourceStride, 0, sample.Height, _destinationData, _destinationStride);

            if (converted <= 0)
            {
                Fail(VideoRecorderFailure.WriteFailed);
                return;
            }
        }

        // Timed by the recorder rather than by the caller: a recording's timeline is its own, and a caller that drops
        // frames must not make it run fast.
        _frame->pts = Interlocked.Read(ref _written);

        if (ffmpeg.avcodec_send_frame(_encoder, _frame) < 0)
        {
            Fail(VideoRecorderFailure.WriteFailed);
            return;
        }

        while (true)
        {
            var received = ffmpeg.avcodec_receive_packet(_encoder, _packet);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (received < 0)
            {
                Fail(VideoRecorderFailure.WriteFailed);
                return;
            }

            _packet->stream_index = _stream->index;
            ffmpeg.av_packet_rescale_ts(_packet, _encoder->time_base, _stream->time_base);

            var written = ffmpeg.av_interleaved_write_frame(_format, _packet);
            ffmpeg.av_packet_unref(_packet);

            if (written < 0)
            {
                Fail(VideoRecorderFailure.WriteFailed);
                return;
            }
        }

        Interlocked.Increment(ref _written);
    }

    /// <summary>Flushes what is left and closes everything, optionally taking the file with it.</summary>
    private unsafe void Close(bool keepFile)
    {
        lock (_gate)
        {
            if (_encoder is not null && keepFile)
            {
                // Anything still inside the encoder — the frames it was holding for reordering — has to come out
                // before the trailer, or the recording ends early.
                _ = ffmpeg.avcodec_send_frame(_encoder, null);
                while (true)
                {
                    if (ffmpeg.avcodec_receive_packet(_encoder, _packet) < 0)
                    {
                        break;
                    }

                    _packet->stream_index = _stream->index;
                    ffmpeg.av_packet_rescale_ts(_packet, _encoder->time_base, _stream->time_base);
                    _ = ffmpeg.av_interleaved_write_frame(_format, _packet);
                    ffmpeg.av_packet_unref(_packet);
                }
            }

            if (_format is not null && keepFile)
            {
                _ = ffmpeg.av_write_trailer(_format);
            }

            var frame = _frame;
            ffmpeg.av_frame_free(&frame);

            var packet = _packet;
            ffmpeg.av_packet_free(&packet);

            var scaler = _scaler;
            if (scaler is not null)
            {
                ffmpeg.sws_freeContext(scaler);
                _scaler = null;
            }

            var encoder = _encoder;
            ffmpeg.avcodec_free_context(&encoder);

            var format = _format;
            if (format is not null)
            {
                if (format->pb is not null)
                {
                    _ = ffmpeg.avio_closep(&format->pb);
                }

                ffmpeg.avformat_free_context(format);
                _format = null;
            }

            if (!keepFile && _outputPath is { Length: > 0 } path && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>One frame on its way to the encoder, copied out of the caller's memory.</summary>
    private readonly record struct RecordSample(byte[] Pixels, int Stride, int Width, int Height);

    /// <summary>Turns an FFmpeg error code into the sentence FFmpeg has for it, so logs say what went wrong.</summary>
    private static unsafe string Describe(int error)
    {
        var buffer = new byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
        fixed (byte* text = buffer)
        {
            ffmpeg.av_strerror(error, text, (ulong)buffer.Length);
            return $"{error}: {Marshal.PtrToStringUTF8((nint)text)}";
        }
    }
}

/// <summary>Creates a recorder per recording, which is how the callers use it.</summary>
[SupportedOSPlatform("linux")]
internal sealed class FfmpegVideoRecorderFactory : IVideoRecorderFactory
{
    public IVideoRecorder Create() => new FfmpegVideoRecorder();
}
