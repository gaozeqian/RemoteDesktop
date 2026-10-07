using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Channels;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Windows.MediaFoundation;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.Recording;

[SupportedOSPlatform("windows")]
public sealed class MfVideoRecorderFactory : IVideoRecorderFactory
{
    public IVideoRecorder Create() => new MfVideoRecorder();
}

[SupportedOSPlatform("windows")]
public sealed class MfVideoRecorder : IVideoRecorder
{
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = new();
    private IMFSinkWriter? _writer;
    private int _streamIndex;
    private int _width;
    private int _height;
    private int _frameRate;
    private long _frameDurationTicks;
    private long _lastTimestampTicks = -1;
    private Channel<RecordSample>? _channel;
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

            MfRuntime.EnsureStarted();

            _filePath = filePath;
            _width = width;
            _height = height;
            _frameRate = Math.Clamp(frameRate, 1, 60);
            _frameDurationTicks = TimeSpan.TicksPerSecond / _frameRate;
            _lastTimestampTicks = -1;
            Interlocked.Exchange(ref _writtenFrames, 0);
            Volatile.Write(ref _failure, (int)VideoRecorderFailure.None);

            var writer = MediaFactory.MFCreateSinkWriterFromURL(filePath, null, null);
            try
            {
                using var outputType = MfRuntime.CreateVideoType(MfGuids.VideoFormatH264, _width, _height, _frameRate);
                outputType.Set(MfGuids.AvgBitrate, (uint)bitrateKbps * 1000).CheckError();
                _streamIndex = writer.AddStream(outputType);

                using var inputType = MfRuntime.CreateVideoType(VideoFormatGuids.Rgb32, _width, _height, _frameRate);
                writer.SetInputMediaType(_streamIndex, inputType, null);

                writer.BeginWriting();
                _writer = writer;
            }
            catch
            {
                writer.Dispose();
                throw;
            }

            var channel = Channel.CreateBounded<RecordSample>(new BoundedChannelOptions(30)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            _channel = channel;

            _isRecording = true;
            _clock.Restart();
            // Both are handed over explicitly: the fields are cleared the moment a stop begins, and the worker must
            // keep draining into the writer it started with rather than see them disappear under it.
            _worker = Task.Run(() => ProcessQueueAsync(channel, writer));
        }
    }

    public void RecordFrame(ReadOnlySpan<byte> bgraPixels, int stride, int width, int height)
    {
        // A stop on another thread clears the channel: hold on to the one this frame belongs to. Writing into a
        // completed channel is a no-op, which is the right outcome for a frame that arrived as the recording ended.
        var channel = _channel;
        if (!_isRecording || channel is null)
        {
            return;
        }

        if (width != _width || height != _height)
        {
            // The encoder's input type is fixed when the recording starts, so a picture that changed size can never
            // go into this file. Report it instead of dropping every frame in silence behind a running clock.
            Fail(VideoRecorderFailure.PictureSizeChanged);
            return;
        }

        var rowBytes = width * 4;
        var totalBytes = rowBytes * height;
        if (bgraPixels.Length < ((height - 1) * stride) + rowBytes)
        {
            return;
        }

        var buffer = new byte[totalBytes];
        for (var y = 0; y < height; y++)
        {
            bgraPixels.Slice(y * stride, rowBytes).CopyTo(buffer.AsSpan(y * rowBytes, rowBytes));
        }

        var ticks = _clock.Elapsed.Ticks;
        lock (_lock)
        {
            if (ticks <= _lastTimestampTicks)
            {
                ticks = _lastTimestampTicks + 1;
            }

            _lastTimestampTicks = ticks;
        }

        channel.Writer.TryWrite(new RecordSample(buffer, ticks, _frameDurationTicks));
    }

    public async Task<string> StopAsync()
    {
        Channel<RecordSample>? channel;
        Task? worker;
        IMFSinkWriter? writer;
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
            writer = _writer;
            _writer = null;
        }

        // Drain before finalizing. The worker holds its own references to the channel and the writer, so the frames
        // still queued here are the last moments of the recording — discarding them would silently truncate it.
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
                // Ignore worker drain errors on shutdown
            }
        }

        if (writer is not null)
        {
            var frames = WrittenFrames;
            try
            {
                // Media Foundation refuses to finalize a sink that never saw a sample (MF_E_SINK_NO_SAMPLES_PROCESSED),
                // so that case is decided here rather than left to fail inside Finalize.
                if (frames > 0)
                {
                    writer.NotifyEndOfSegment(_streamIndex);
                    writer.Finalize();
                }
            }
            finally
            {
                writer.Dispose();
            }

            if (frames == 0)
            {
                // Whatever the writer left behind is not a recording. Taking it away beats telling the user an MP4
                // was saved that no player will open. Failure says so, because a caller that catches this has
                // nothing else to tell the two cases apart.
                Fail(VideoRecorderFailure.NothingRecorded);
                TryDeleteFile(resultPath);
                throw new InvalidOperationException("No frame reached the encoder, so there is no recording to save.");
            }
        }

        return resultPath;
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

    public async ValueTask DisposeAsync()
    {
        if (_isRecording)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessQueueAsync(Channel<RecordSample> channel, IMFSinkWriter writer)
    {
        var reader = channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var sample))
            {
                try
                {
                    using var mfSample = MfRuntime.CreateSample(sample.Pixels, sample.TimestampTicks, sample.DurationTicks);
                    writer.WriteSample(_streamIndex, mfSample);
                    Interlocked.Increment(ref _writtenFrames);
                }
                catch (Exception)
                {
                    // Disk full, encoder error: the recording cannot go on. The caller finds out through Failure
                    // rather than by noticing later that the file stopped growing.
                    Fail(VideoRecorderFailure.WriteFailed);
                    return;
                }
            }
        }
    }

    /// <summary>The first reason wins: it is the one that explains where the file ends.</summary>
    private void Fail(VideoRecorderFailure failure) =>
        Interlocked.CompareExchange(ref _failure, (int)failure, (int)VideoRecorderFailure.None);

    private sealed record RecordSample(byte[] Pixels, long TimestampTicks, long DurationTicks);
}
