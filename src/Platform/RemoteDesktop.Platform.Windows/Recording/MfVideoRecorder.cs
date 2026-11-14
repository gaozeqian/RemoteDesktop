using System.Diagnostics;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Shared;
using RemoteDesktop.Platform.Windows.MediaFoundation;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.Recording;

[SupportedOSPlatform("windows")]
internal sealed class MfVideoRecorderFactory : IVideoRecorderFactory
{
    public IVideoRecorder Create() => new MfVideoRecorder();
}

[SupportedOSPlatform("windows")]
internal sealed class MfVideoRecorder : IVideoRecorder
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
    private RecorderFrameQueue<RecordSample>? _queue;
    private volatile bool _isRecording;
    private string? _filePath;

    public bool IsRecording => _isRecording;

    public string? OutputFilePath => _filePath;

    public TimeSpan Duration => _clock.Elapsed;

    public long WrittenFrames => _queue?.WrittenFrames ?? 0;

    public VideoRecorderFailure Failure => _queue?.Failure ?? VideoRecorderFailure.None;

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

            // A start that fails must report no failure from the previous, already-stopped recording.
            _queue = null;

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

            // The worker captures the writer it started with, not the field: a stop clears the field, and the
            // frames still queued must drain into the sink that was opened for them.
            _queue = new RecorderFrameQueue<RecordSample>(sample =>
            {
                using var mfSample = MfRuntime.CreateSample(sample.Pixels, sample.TimestampTicks, sample.DurationTicks);
                writer.WriteSample(_streamIndex, mfSample);
                return null;
            });
            _isRecording = true;
            _clock.Restart();
        }
    }

    public void RecordFrame(ReadOnlySpan<byte> bgraPixels, int stride, int width, int height)
    {
        // A stop on another thread clears the channel: hold on to the one this frame belongs to. Writing into a
        // completed channel is a no-op, which is the right outcome for a frame that arrived as the recording ended.
        var queue = _queue;
        if (!_isRecording || queue is null)
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
        if (stride < rowBytes || bgraPixels.Length < ((height - 1) * stride) + rowBytes)
        {
            // A malformed frame is dropped, not reported as a write failure: the encoder never saw it, and the next
            // well-formed frame still goes into the same file. Linux and macOS behave the same way.
            return;
        }

        // The rows go in bottom-up: Media Foundation reads RGB32 in system memory with the DIB convention, so a
        // top-down frame handed over as-is comes out of the file upside down.
        var buffer = new byte[totalBytes];
        for (var y = 0; y < height; y++)
        {
            bgraPixels.Slice(y * stride, rowBytes).CopyTo(buffer.AsSpan((height - 1 - y) * rowBytes, rowBytes));
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

        queue.TryEnqueue(new RecordSample(buffer, ticks, _frameDurationTicks));
    }

    public async Task<string> StopAsync()
    {
        RecorderFrameQueue<RecordSample>? queue;
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
            queue = _queue;
            writer = _writer;
            _writer = null;
        }

        // Drain before finalizing. The frames still queued are the last moments of the recording — discarding them
        // would silently truncate it. The queue is kept after the stop so WrittenFrames and Failure still answer.
        if (queue is not null)
        {
            queue.Complete();
            await queue.DrainAsync().ConfigureAwait(false);
        }

        if (writer is not null)
        {
            var frames = queue?.WrittenFrames ?? 0;
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
                queue?.Fail(VideoRecorderFailure.NothingRecorded);
                RecorderFile.TryDelete(resultPath);
                throw new InvalidOperationException("No frame reached the encoder, so there is no recording to save.");
            }
        }

        return resultPath;
    }

    /// <summary>
    /// Stops a running recording, discarding the "nothing was recorded" error: disposal has no one to report it to,
    /// and the file has already been removed. The same shape as the Linux and macOS recorders.
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


    /// <summary>The first reason wins: it is the one that explains where the file ends.</summary>
    private void Fail(VideoRecorderFailure failure) => _queue?.Fail(failure);

    private sealed record RecordSample(byte[] Pixels, long TimestampTicks, long DurationTicks);
}
