using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Windows.MediaFoundation;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.Camera;

/// <summary>Finds the video capture devices Media Foundation knows (USB webcams, built-in cameras, virtual cameras) and the modes they offer.</summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationCameraEnumerator : ICameraEnumerator
{
    private const int MinWidth = 160;
    private const int MinFrameRate = 5;

    public IReadOnlyList<CameraInfo> GetCameras()
    {
        MfRuntime.EnsureStarted();
        var cameras = new List<CameraInfo>();
        foreach (var (id, name) in MediaFoundationCameraSource.EnumerateDevices())
        {
            cameras.Add(new CameraInfo(id, name, ReadModes(id)));
        }

        return cameras;
    }

    // A device that cannot be opened (in use, blocked) is still listed, without modes; opening it later reports the reason.
    private static List<CameraMode> ReadModes(string id)
    {
        try
        {
            using var source = MediaFoundationCameraSource.CreateMediaSource(id);
            using var attributes = MediaFactory.MFCreateAttributes(1);
            using var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, attributes);
            var modes = new HashSet<CameraMode>();
            for (var i = 0; ; i++)
            {
                IMFMediaType type;
                try
                {
                    type = reader.GetNativeMediaType(MediaFoundationCameraSource.FirstVideoStream, i);
                }
                catch (SharpGenException)
                {
                    break; // MF_E_NO_MORE_TYPES
                }

                using (type)
                {
                    MediaFactory.MFGetAttributeSize(type, MfGuids.FrameSize, out var width, out var height);
                    MediaFactory.MFGetAttributeRatio(type, MfGuids.FrameRate, out var numerator, out var denominator);
                    var frameRate = denominator == 0 ? 0 : (int)Math.Round((double)numerator / denominator);
                    if (width >= MinWidth && height >= MinWidth * 3 / 4 && frameRate >= MinFrameRate)
                    {
                        modes.Add(new CameraMode((int)width & ~1, (int)height & ~1, frameRate));
                    }
                }
            }

            return [.. modes.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.FrameRate)];
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
        {
            return [];
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class MediaFoundationCameraSourceFactory(ICameraEnumerator cameras) : ICameraSourceFactory
{
    public ICameraSource Open(string cameraId)
    {
        var camera = cameras.GetCameras().FirstOrDefault(c => c.Id == cameraId)
            ?? throw new CameraUnavailableException("That camera is not connected.");
        return new MediaFoundationCameraSource(camera);
    }
}

/// <summary>
/// One camera, read through a Media Foundation source reader that converts whatever the camera delivers (MJPEG, YUY2...)
/// to NV12. A dedicated thread reads continuously and keeps only the newest picture, so a slow consumer sees fresh
/// frames instead of a growing backlog.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MediaFoundationCameraSource(CameraInfo camera) : ICameraSource
{
    /// <summary>MF_SOURCE_READER_FIRST_VIDEO_STREAM.</summary>
    public const int FirstVideoStream = unchecked((int)0xFFFFFFFC);

    private const uint ErrorAccessDenied = 0x80070005;

    public CameraInfo Camera { get; } = camera;

    public static IEnumerable<(string Id, string Name)> EnumerateDevices()
    {
        MfRuntime.EnsureStarted();
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap).CheckError();
        var devices = new List<(string, string)>();
        var activates = MediaFactory.MFEnumDeviceSources(attributes);
        foreach (var activate in activates)
        {
            using (activate)
            {
                var id = activate.GetString(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink);
                var name = activate.GetString(CaptureDeviceAttributeKeys.FriendlyName);
                devices.Add((id, name));
            }
        }

        return devices;
    }

    public static IMFMediaSource CreateMediaSource(string symbolicLink)
    {
        MfRuntime.EnsureStarted();
        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap).CheckError();
        attributes.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, symbolicLink).CheckError();
        return MediaFactory.MFCreateDeviceSource(attributes);
    }

    public async IAsyncEnumerable<VideoFrame> CaptureAsync(CameraMode mode, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mode);

        // Only the newest picture is kept; the ones it replaces are released right away.
        var frames = Channel.CreateBounded<MemoryVideoFrame>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
            dropped => dropped.Dispose());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Its own token: leaving the loop (dispose, break) has to stop the camera even when the caller never cancels.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = Task.Factory.StartNew(
            () => ReadLoop(mode, frames.Writer, started, stop.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            // A camera that cannot be opened fails here, before the caller has set anything up.
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            // The reader thread ends with the token (within one frame); frames still queued are released.
            await stop.CancelAsync().ConfigureAwait(false);
            await reader.ConfigureAwait(false);
            while (frames.Reader.TryRead(out var left))
            {
                left.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ReadLoop(CameraMode mode, ChannelWriter<MemoryVideoFrame> writer, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        IMFMediaSource? source = null;
        IMFSourceReader? reader = null;
        try
        {
            source = CreateMediaSource(Camera.Id);
            using (var attributes = MediaFactory.MFCreateAttributes(2))
            {
                // Lets the reader insert the decoder / colour converter between the camera's format and NV12.
                attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u).CheckError();
                reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, attributes);
            }

            var (width, height) = Configure(reader, mode);
            started.TrySetResult();

            var clock = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested)
            {
                using var sample = reader.ReadSample(FirstVideoStream, 0, out _, out var flags, out _);
                if (flags.HasFlag(SourceReaderFlag.EndOfStream) || flags.HasFlag(SourceReaderFlag.Error))
                {
                    throw new CameraUnavailableException("The camera stopped delivering pictures (unplugged or taken by another program).");
                }

                if (flags.HasFlag(SourceReaderFlag.CurrentMediaTypeChanged))
                {
                    (width, height) = CurrentSize(reader);
                }

                if (sample is null)
                {
                    continue; // a gap in the stream; the next read waits for the next picture
                }

                writer.TryWrite(ToFrame(sample, width, height, clock.Elapsed));
            }
        }
        catch (CameraUnavailableException ex)
        {
            Fail(ex, started, writer);
            return;
        }
        catch (SharpGenException ex)
        {
            Fail(Describe(ex), started, writer);
            return;
        }
        finally
        {
            reader?.Dispose();
            if (source is not null)
            {
                try
                {
                    source.Shutdown();
                }
                catch (SharpGenException)
                {
                    // Already gone with the device.
                }

                source.Dispose();
            }
        }

        writer.TryComplete();
    }

    // Before the first picture the caller is still waiting to start; afterwards the stream just ends with the error.
    private static void Fail(CameraUnavailableException failure, TaskCompletionSource started, ChannelWriter<MemoryVideoFrame> writer)
    {
        if (!started.TrySetException(failure))
        {
            writer.TryComplete(failure);
        }
    }

    // Picks the camera's own format closest to the request, then asks the reader to deliver NV12 of that size.
    private static (int Width, int Height) Configure(IMFSourceReader reader, CameraMode mode)
    {
        IMFMediaType? best = null;
        var bestScore = double.MaxValue;
        for (var i = 0; ; i++)
        {
            IMFMediaType type;
            try
            {
                type = reader.GetNativeMediaType(FirstVideoStream, i);
            }
            catch (SharpGenException)
            {
                break;
            }

            MediaFactory.MFGetAttributeSize(type, MfGuids.FrameSize, out var w, out var h);
            MediaFactory.MFGetAttributeRatio(type, MfGuids.FrameRate, out var n, out var d);
            var fps = d == 0 ? 0 : (double)n / d;
            var score = Math.Abs(((double)w * h) - ((double)mode.Width * mode.Height)) + (Math.Abs(fps - mode.FrameRate) * 1000);
            if (score < bestScore)
            {
                best?.Dispose();
                best = type;
                bestScore = score;
            }
            else
            {
                type.Dispose();
            }
        }

        if (best is null)
        {
            throw new CameraUnavailableException("The camera offers no video format.");
        }

        using (best)
        {
            MediaFactory.MFGetAttributeSize(best, MfGuids.FrameSize, out var width, out var height);
            MediaFactory.MFGetAttributeRatio(best, MfGuids.FrameRate, out var numerator, out var denominator);
            reader.SetStreamSelection(FirstVideoStream, true);
            reader.SetCurrentMediaType(FirstVideoStream, best);

            using var output = MfRuntime.CreateVideoType(MfGuids.VideoFormatNv12, (int)width, (int)height, 0);
            MediaFactory.MFSetAttributeRatio(output, MfGuids.FrameRate, numerator, Math.Max(1, denominator)).CheckError();
            reader.SetCurrentMediaType(FirstVideoStream, output);
            return (CurrentSize(reader).Width, CurrentSize(reader).Height);
        }
    }

    private static (int Width, int Height) CurrentSize(IMFSourceReader reader)
    {
        using var type = reader.GetCurrentMediaType(FirstVideoStream);
        MediaFactory.MFGetAttributeSize(type, MfGuids.FrameSize, out var width, out var height);
        return ((int)width, (int)height);
    }

    // The buffer holds the Y plane and the interleaved UV plane. Its rows may be padded; the padding is left behind.
    private static unsafe MemoryVideoFrame ToFrame(IMFSample sample, int width, int height, TimeSpan timestamp)
    {
        var evenWidth = width & ~1;
        var evenHeight = height & ~1;
        var frame = new MemoryVideoFrame(evenWidth, evenHeight, PixelFormat.Nv12, timestamp);
        try
        {
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var pointer, out _, out var length);
            try
            {
                var source = new ReadOnlySpan<byte>((void*)pointer, length);
                var stride = width;
                var paddedHeight = height;
                if (length != width * height * 3 / 2 && length % 3 == 0 && length * 2 / 3 % height == 0)
                {
                    // Rows padded to an alignment: Y is stride x paddedHeight rows, then UV.
                    stride = length * 2 / 3 / height;
                }

                var destination = frame.Buffer;
                for (var y = 0; y < evenHeight; y++)
                {
                    source.Slice(y * stride, evenWidth).CopyTo(destination.Slice(y * frame.Stride, evenWidth));
                }

                var sourceUv = paddedHeight * stride;
                var destinationUv = evenHeight * frame.Stride;
                for (var y = 0; y < evenHeight / 2; y++)
                {
                    source.Slice(sourceUv + (y * stride), evenWidth).CopyTo(destination.Slice(destinationUv + (y * frame.Stride), evenWidth));
                }
            }
            finally
            {
                buffer.Unlock();
            }

            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static CameraUnavailableException Describe(SharpGenException ex) =>
        (uint)ex.HResult == ErrorAccessDenied
            ? new CameraUnavailableException("Windows does not let this program use the camera. Allow desktop apps to access the camera in Settings > Privacy & security > Camera.", ex)
            : new CameraUnavailableException("The camera could not be opened. It may be in use by another program.", ex);
}
