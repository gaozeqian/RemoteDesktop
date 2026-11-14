using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Recording;

/// <summary>
/// Records BGRA32 frames into an H.264 MP4, timed by the recorder itself.
/// </summary>
/// <remarks>
/// The heavy lifting — H.264 compression and the MP4 mux — is AVAssetWriter, driven through the native helper. This
/// class owns the session lifecycle and a single worker that drains frames into the helper, the same shape as the
/// Windows <c>MfVideoRecorder</c> so the two behave identically above the platform line. The contract says frames are
/// top-row-first, and CVPixelBuffer memory is too, so they go in without the bottom-up flip Media Foundation needs.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacVideoRecorder : IVideoRecorder
{
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = new();
    private nint _recorder;
    private int _width;
    private int _height;
    private int _frameRate;
    private Channel<QueuedFrame>? _channel;
    private Task? _worker;
    private volatile bool _isRecording;
    private string? _filePath;
    private long _writtenFrames;
    private int _failure;

    public bool IsRecording => _isRecording;

    public string? OutputFilePath => _filePath;

    public TimeSpan Duration => _clock.Elapsed;

    public long WrittenFrames => Interlocked.Read(ref _writtenFrames);

    public VideoRecorderFailure Failure => (VideoRecorderFailure)Volatile.Read(ref _failure);

    public void Start(string filePath, int width, int height, int frameRate = 30, int bitrateKbps = 4000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Dimensions must be positive.", nameof(width));
        }

        lock (_lock)
        {
            if (_isRecording)
            {
                throw new InvalidOperationException("Recording is already in progress.");
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _filePath = filePath;
            _width = width;
            _height = height;
            _frameRate = Math.Clamp(frameRate, 1, 60);
            Interlocked.Exchange(ref _writtenFrames, 0);
            Volatile.Write(ref _failure, (int)VideoRecorderFailure.None);

            var error = new byte[MacNativeInterop.ErrorBufferSize];
            int status;
            nint recorder;
            string? failure = null;
            unsafe
            {
                fixed (byte* errorPointer = error)
                {
                    status = MacNativeInterop.rd_recorder_create(
                        filePath, width, height, _frameRate, bitrateKbps, out recorder, errorPointer,
                        (nuint)error.Length);
                    if (status != MacNativeInterop.Ok)
                    {
                        failure = MacNativeInterop.ReadError(errorPointer, error.Length);
                    }
                }
            }

            if (status != MacNativeInterop.Ok)
            {
                _filePath = null;
                throw new InvalidOperationException(
                    $"Could not start the recording: {failure}");
            }

            _recorder = recorder;

            var channel = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(30)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            _channel = channel;

            _isRecording = true;
            _clock.Restart();
            // The channel and the handle are cleared the moment a stop begins, so the worker must hold its own copies.
            _worker = Task.Run(() => ProcessQueueAsync(channel, recorder));
        }
    }

    public void RecordFrame(ReadOnlySpan<byte> bgraPixels, int stride, int width, int height)
    {
        var channel = _channel;
        if (!_isRecording || channel is null)
        {
            return;
        }

        if (width != _width || height != _height)
        {
            // The writer's input is fixed when the recording starts, so this frame can never enter the file. Report it
            // rather than drop it silently behind a running clock.
            Fail(VideoRecorderFailure.PictureSizeChanged);
            return;
        }

        var rowBytes = width * 4;
        var totalBytes = rowBytes * height;
        if (stride < rowBytes || bgraPixels.Length < ((height - 1) * stride) + rowBytes)
        {
            // A malformed frame is dropped, not reported as a write failure: the encoder never saw it, and the next
            // well-formed frame still goes into the same file. Windows and Linux behave the same way.
            return;
        }

        // Owned copy: the worker holds the buffer while the native call runs, and the caller's span may be reused the
        // instant this method returns. Row-by-row keeps only the visible width, not any padding the caller carries.
        var buffer = new byte[totalBytes];
        for (var y = 0; y < height; y++)
        {
            bgraPixels.Slice(y * stride, rowBytes).CopyTo(buffer.AsSpan(y * rowBytes, rowBytes));
        }

        channel.Writer.TryWrite(new QueuedFrame(buffer, rowBytes, width, height));
    }

    public async Task<string> StopAsync()
    {
        Channel<QueuedFrame>? channel;
        Task? worker;
        nint recorder;
        var resultPath = _filePath ?? string.Empty;

        lock (_lock)
        {
            if (!_isRecording)
            {
                return resultPath;
            }

            _isRecording = false;
            _clock.Stop();
            channel = _channel;
            _channel = null;
            worker = _worker;
            _worker = null;
            recorder = _recorder;
            _recorder = 0;
        }

        // Drain before finalizing: the worker holds the handle and the channel it is draining, so the still-queued
        // frames are the last moments of the recording — discarding them would silently truncate it.
        if (channel is not null)
        {
            channel.Writer.TryComplete();
        }

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch
            {
                // Ignore worker drain errors on shutdown.
            }
        }

        if (recorder != 0)
        {
            var error = new byte[MacNativeInterop.ErrorBufferSize];
            int status;
            long wrote;
            unsafe
            {
                fixed (byte* errorPointer = error)
                {
                    status = MacNativeInterop.rd_recorder_finish(recorder, out wrote, errorPointer, (nuint)error.Length);
                }
            }

            MacNativeInterop.rd_recorder_destroy(recorder);

            if (wrote == 0)
            {
                // Nothing ever reached the writer: there is no recording, so take the file away rather than leave an
                // unplayable stub behind.
                Fail(VideoRecorderFailure.NothingRecorded);
                TryDeleteFile(resultPath);
                throw new InvalidOperationException("No frame reached the encoder, so there is no recording to save.");
            }

            if (status != MacNativeInterop.Ok)
            {
                TryDeleteFile(resultPath);
                string? detail = null;
                unsafe
                {
                    fixed (byte* errorPointer = error)
                    {
                        detail = MacNativeInterop.ReadError(errorPointer, error.Length);
                    }
                }
                throw new InvalidOperationException(
                    $"The recording could not be saved: {detail}");
            }
        }

        return resultPath;
    }

    /// <summary>
    /// Stops a running recording, discarding the "nothing was recorded" error: disposal has no one to report it to,
    /// and the file has already been removed. The same shape as the Windows and Linux recorders.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isRecording)
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving an unplayable file behind is better than throwing while closing one that never started.
        }
    }

    private async Task ProcessQueueAsync(Channel<QueuedFrame> channel, nint recorder)
    {
        var reader = channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var frame))
            {
                try
                {
                    var status = MacNativeInterop.Ok;
                    unsafe
                    {
                        fixed (byte* pixels = frame.Pixels)
                        {
                            var error = new byte[MacNativeInterop.ErrorBufferSize];
                            fixed (byte* errorPointer = error)
                            {
                                status = MacNativeInterop.rd_recorder_append(
                                    recorder,
                                    pixels,
                                    (nuint)frame.Pixels.Length,
                                    frame.RowBytes,
                                    frame.Width,
                                    frame.Height,
                                    errorPointer,
                                    (nuint)error.Length);
                            }
                        }
                    }

                    if (status != MacNativeInterop.Ok)
                    {
                        // Disk full, encoder error, backpressure that would not clear: the recording cannot go on. The
                        // caller finds out through Failure rather than by noticing the file stopped growing.
                        Fail(VideoRecorderFailure.WriteFailed);
                        return;
                    }

                    Interlocked.Increment(ref _writtenFrames);
                }
                catch (Exception)
                {
                    Fail(VideoRecorderFailure.WriteFailed);
                    return;
                }
            }
        }
    }

    /// <summary>The first reason wins: it is the one that explains where the file ends.</summary>
    private void Fail(VideoRecorderFailure failure) =>
        Interlocked.CompareExchange(ref _failure, (int)failure, (int)VideoRecorderFailure.None);

    /// <summary>
    /// The pixels are already repacked to <c>RowBytes = Width * 4</c>, so that is the stride the native call must be
    /// given; the caller's stride (possibly padded) describes a buffer that no longer exists.
    /// </summary>
    private sealed record QueuedFrame(byte[] Pixels, int RowBytes, int Width, int Height);
}
