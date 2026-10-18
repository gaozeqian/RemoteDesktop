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

            var (width, height, yuy2) = Configure(reader, mode);
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
                    (width, height, yuy2) = CurrentFormat(reader);
                }

                if (sample is null)
                {
                    continue; // a gap in the stream; the next read waits for the next picture
                }

                writer.TryWrite(ToFrame(sample, width, height, yuy2, clock.Elapsed));
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

    // Picks the camera's own format closest to the request, then asks the reader to deliver NV12 of that size. Some
    // cameras only offer YUY2 at their smaller sizes and the reader refuses to convert those to NV12; for them the
    // native 4:2:2 layout comes back and the chroma is reduced by hand, so the contract is still NV12.
    private static (int Width, int Height, bool Yuy2) Configure(IMFSourceReader reader, CameraMode mode)
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
            try
            {
                reader.SetCurrentMediaType(FirstVideoStream, output);
                return CurrentFormat(reader);
            }
            catch (SharpGenException)
            {
                // The driver (the built-in webcam in the E2E machine is one) offers YUY2 for its small modes and
                // answers MF_E_INVALIDMEDIATYPE for an NV12 output. Ask for YUY2 instead; ToFrame reduces it.
                using var yuy2 = MfRuntime.CreateVideoType(MfGuids.VideoFormatYuy2, (int)width, (int)height, 0);
                MediaFactory.MFSetAttributeRatio(yuy2, MfGuids.FrameRate, numerator, Math.Max(1, denominator)).CheckError();
                reader.SetCurrentMediaType(FirstVideoStream, yuy2);
                return CurrentFormat(reader);
            }
        }
    }

    private static (int Width, int Height, bool Yuy2) CurrentFormat(IMFSourceReader reader)
    {
        using var type = reader.GetCurrentMediaType(FirstVideoStream);
        MediaFactory.MFGetAttributeSize(type, MfGuids.FrameSize, out var width, out var height);
        return ((int)width, (int)height, type.GetGUID(MfGuids.Subtype) == MfGuids.VideoFormatYuy2);
    }

    // The frame contract is NV12. The reader hands out NV12 when the camera/driver can convert, and YUY2 for the
    // modes where it cannot (see Configure); both end up in the same plane layout here.
    private static unsafe MemoryVideoFrame ToFrame(IMFSample sample, int width, int height, bool yuy2, TimeSpan timestamp)
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
                if (yuy2)
                {
                    CopyYuy2ToNv12(source, evenWidth, evenHeight, frame);
                }
                else
                {
                    CopyNv12(source, width, height, evenWidth, evenHeight, frame);
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

    // The buffer holds the Y plane and the interleaved UV plane. Its rows may be padded; the padding is left behind.
    private static void CopyNv12(ReadOnlySpan<byte> source, int width, int height, int evenWidth, int evenHeight, MemoryVideoFrame frame)
    {
        var stride = width;
        var paddedHeight = height;
        if (source.Length != width * height * 3 / 2 && source.Length % 3 == 0 && source.Length * 2 / 3 % height == 0)
        {
            // Rows padded to an alignment: Y is stride x paddedHeight rows, then UV.
            stride = source.Length * 2 / 3 / height;
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

    /// <summary>
    /// Reduces the camera's YUY2 (4:2:2) buffer to NV12 (4:2:0): luma passes through, and each chroma sample is
    /// averaged over the two rows it now covers. Only the bit layout changes, so no colour matrix is involved.
    /// </summary>
    internal static void CopyYuy2ToNv12(ReadOnlySpan<byte> source, int width, int height, MemoryVideoFrame frame)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);

        var rowBytes = width * 2;
        if (source.Length < rowBytes * height)
        {
            throw new InvalidDataException("The camera's YUY2 buffer is smaller than its picture.");
        }

        // Rows may be padded to an alignment; the buffer's own length says by how much.
        var stride = source.Length % height == 0 ? source.Length / height : rowBytes;
        if (stride < rowBytes)
        {
            stride = rowBytes;
        }

        var destination = frame.Buffer;
        for (var y = 0; y < height; y++)
        {
            var row = source.Slice(y * stride, rowBytes);
            var luma = destination.Slice(y * frame.Stride, width);
            for (var x = 0; x < width; x++)
            {
                luma[x] = row[x * 2];
            }
        }

        var uvOffset = height * frame.Stride;
        for (var y = 0; y < height; y += 2)
        {
            var first = source.Slice(y * stride, rowBytes);
            var second = source.Slice((y + 1) * stride, rowBytes);
            var uv = destination.Slice(uvOffset + (y / 2) * frame.Stride, width);
            for (var x = 0; x < width; x += 2)
            {
                var pair = x * 2;
                uv[x] = (byte)((first[pair + 1] + second[pair + 1] + 1) / 2);
                uv[x + 1] = (byte)((first[pair + 3] + second[pair + 3] + 1) / 2);
            }
        }
    }

    private static CameraUnavailableException Describe(SharpGenException ex) =>
        (uint)ex.HResult == ErrorAccessDenied
            ? new CameraUnavailableException("Windows does not let this program use the camera. Allow desktop apps to access the camera in Settings > Privacy & security > Camera.", ex)
            : new CameraUnavailableException("The camera could not be opened. It may be in use by another program.", ex);
}
