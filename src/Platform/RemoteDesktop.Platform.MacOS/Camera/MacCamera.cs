using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Camera;

/// <summary>
/// Finds the cameras AVFoundation knows (the built-in one, USB webcams, Continuity Camera, Desk View) and the
/// picture sizes they offer.
/// </summary>
/// <remarks>
/// The mode list is built natively because AVFoundation is the only thing that can answer it, and it is filtered
/// there with the same floor the Windows enumerator applies: below 160 points wide, or below 5 frames a second, is
/// not a mode a remote viewer would choose, and virtual cameras advertise a long tail of such sizes.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacCameraEnumerator : ICameraEnumerator
{
    public IReadOnlyList<CameraInfo> GetCameras()
    {
        // Every id and name is copied out of the helper's storage before the first mode query. That is not
        // defensive style: rd_camera_modes is itself a listing call, and a listing call resets the storage the
        // previous one handed out — so a loop that read a camera's name after asking for its modes would be reading
        // freed memory from the second camera onwards.
        var listed = ReadCameras();

        var cameras = new List<CameraInfo>(listed.Count);
        foreach (var (id, name) in listed)
        {
            cameras.Add(new CameraInfo(id, name, ReadModes(id)));
        }

        return cameras;
    }

    private static unsafe List<(string Id, string Name)> ReadCameras()
    {
        var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
        error[0] = 0;

        var status = MacNativeInterop.rd_camera_list(
            out var list, out var count, error, MacNativeInterop.ErrorBufferSize);

        if (status != MacNativeInterop.Ok)
        {
            throw new CameraUnavailableException(
                MacNativeInterop.Describe(status, error, MacNativeInterop.ErrorBufferSize));
        }

        var cameras = new List<(string, string)>(count);
        for (var i = 0; i < count; i++)
        {
            var id = Marshal.PtrToStringUTF8((nint)list[i].Id) ?? string.Empty;
            var name = Marshal.PtrToStringUTF8((nint)list[i].Name) ?? string.Empty;
            if (id.Length > 0)
            {
                cameras.Add((id, name));
            }
        }

        return cameras;
    }

    /// <summary>
    /// A camera that cannot be opened right now (in use elsewhere, or unplugged between the two calls) is still
    /// listed, without modes. That is what the Windows enumerator does too, and the reason it cannot be used is
    /// reported when it is actually opened rather than guessed at here.
    /// </summary>
    private static unsafe List<CameraMode> ReadModes(string cameraId)
    {
        var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
        error[0] = 0;

        var status = MacNativeInterop.rd_camera_modes(
            cameraId, out var list, out var count, error, MacNativeInterop.ErrorBufferSize);

        if (status != MacNativeInterop.Ok)
        {
            return [];
        }

        var modes = new List<CameraMode>(count);
        for (var i = 0; i < count; i++)
        {
            modes.Add(new CameraMode(list[i].Width, list[i].Height, (int)Math.Round(list[i].FrameRate)));
        }

        return modes;
    }
}

/// <summary>Opens one of the cameras the enumerator found.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacCameraSourceFactory(ICameraEnumerator cameras) : ICameraSourceFactory
{
    public ICameraSource Open(string cameraId)
    {
        var camera = cameras.GetCameras().FirstOrDefault(c => c.Id == cameraId)
            ?? throw new CameraUnavailableException("That camera is not connected.");

        return new MacCameraSource(camera);
    }
}

/// <summary>
/// One camera, read through an AVFoundation capture session that delivers BGRA.
/// </summary>
/// <remarks>
/// <para>
/// BGRA is what the rest of the pipeline already carries — the display capturer produces it and the encoder
/// consumes it — so nothing is converted between the camera and the wire.
/// </para>
/// <para>
/// The requested <see cref="CameraMode"/> is a preference, not a guarantee. AVFoundation offers a fixed list of
/// formats per device, so the closest one is chosen natively and the frames that come back report their own size.
/// A caller that needs the exact size must read it from the frame, which is what the pipeline does.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacCameraSource(CameraInfo camera) : ICameraSource
{
    public CameraInfo Camera { get; } = camera;

    public async IAsyncEnumerable<VideoFrame> CaptureAsync(
        CameraMode mode, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mode);

        using var sink = new MacCameraFrameSink();

        // Its own token: leaving the loop (dispose, break) has to stop the camera even when the caller never
        // cancels. The same arrangement the Windows source uses.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Task.Factory.StartNew(
            () => Run(mode, sink, started, stop.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            // A camera that cannot be opened — no permission, in use, unplugged — fails here, before the caller has
            // set anything up.
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            await foreach (var frame in sink.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await session.ConfigureAwait(false);
            sink.Complete();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Runs on a dedicated thread: it opens the camera, starts the session, and then does nothing but hold it open.
    /// </summary>
    /// <remarks>
    /// There is no read loop here, unlike the Windows source. AVFoundation pushes pictures through a delegate, so
    /// the only job left for this thread is to keep the session alive until the consumer is done with it — which is
    /// also why it blocks on the token rather than polling.
    /// </remarks>
    private void Run(CameraMode mode, MacCameraFrameSink sink, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        nint handle = 0;
        var sinkHandle = GCHandle.Alloc(sink);

        try
        {
            // Ask before opening. On a machine where the question was never answered, macOS shows its own prompt
            // naming this app, and the person at the keyboard decides — only they can, since the camera is theirs.
            // Request blocks on this thread (deliberately not the UI thread) until they answer, and returns at once
            // when the answer is already known (denied or restricted). No restart is needed afterwards: unlike
            // Screen Recording, this grant is read on every open. If the answer is still no, the sentence below
            // names the pane where it can be changed; the native helper's own text is English and says less.
            if (!TccGrants.IsGranted(TccGrant.Camera) && !TccGrants.Request(TccGrant.Camera))
            {
                throw new CameraUnavailableException(TccGrants.Describe(TccGrant.Camera));
            }

            handle = Create(mode, sinkHandle);
            Start(handle);
            started.TrySetResult();

            cancellationToken.WaitHandle.WaitOne();
        }
        catch (Exception ex)
        {
            // Ignored when the consumer has already been told the camera started; the stream simply ends.
            started.TrySetException(ex);
        }
        finally
        {
            if (handle != 0)
            {
                // Stop before destroy, so the native queue is quiet before the sink's memory is considered released.
                // rd_camera_destroy also stops, but being explicit is what keeps a picture in flight from writing
                // into a channel that is being torn down.
                MacNativeInterop.rd_camera_stop(handle);
                MacNativeInterop.rd_camera_destroy(handle);
            }

            sinkHandle.Free();
        }
    }

    private unsafe nint Create(CameraMode mode, GCHandle sink)
    {
        var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
        error[0] = 0;

        var status = MacNativeInterop.rd_camera_create(
            Camera.Id,
            mode.Width,
            mode.Height,
            mode.FrameRate,
            MacNativeInterop.CameraFrameCallback,
            GCHandle.ToIntPtr(sink),
            out var handle,
            error,
            MacNativeInterop.ErrorBufferSize);

        if (status != MacNativeInterop.Ok)
        {
            throw new CameraUnavailableException(
                MacNativeInterop.Describe(status, error, MacNativeInterop.ErrorBufferSize));
        }

        return handle;
    }

    private static unsafe void Start(nint handle)
    {
        var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
        error[0] = 0;

        var status = MacNativeInterop.rd_camera_start(handle, error, MacNativeInterop.ErrorBufferSize);
        if (status != MacNativeInterop.Ok)
        {
            throw new CameraUnavailableException(
                MacNativeInterop.Describe(status, error, MacNativeInterop.ErrorBufferSize));
        }
    }
}
