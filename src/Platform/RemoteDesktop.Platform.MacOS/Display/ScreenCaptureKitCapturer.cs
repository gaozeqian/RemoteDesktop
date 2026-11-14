using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Display;

/// <summary>Creates ScreenCaptureKit capturers.</summary>
[SupportedOSPlatform("macos")]
internal sealed class ScreenCaptureKitCapturerFactory : IScreenCapturerFactory
{
    public IScreenCapturer Create(DisplayInfo display) => new ScreenCaptureKitCapturer(display);
}

/// <summary>
/// Captures one display with ScreenCaptureKit.
/// </summary>
/// <remarks>
/// <para>
/// Frames arrive on the helper's serial queue, are copied into pooled memory there, and are handed out by
/// <see cref="CaptureAsync"/>. The copy is unavoidable and is the reason the queue exists: ScreenCaptureKit lends
/// the pixel buffer to the callback and takes it back the moment the callback returns, so anything the consumer
/// wants to look at later has to be moved out first.
/// </para>
/// <para>
/// ScreenCaptureKit reports an unchanged screen as a frame with status <c>Idle</c>, which the helper drops. So
/// "no frame arrived before the timeout" is exactly the contract's "nothing changed", and it is what lets the
/// pipeline slow down for static content without any polling of its own.
/// </para>
/// <para>
/// The pointer is not composited into the image. macOS never reports its shape through ScreenCaptureKit, so the
/// shape comes from NSCursor in the helper and the position from CoreGraphics; the controller then draws the real
/// pointer, which is what the Windows capturer does too. See <see cref="CursorShape"/> for the one thing macOS
/// cannot tell us.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class ScreenCaptureKitCapturer : IScreenCapturer
{
    /// <summary>Enough for a smooth live view without asking the encoder for frames it cannot keep up with.</summary>
    private const int FramesPerSecond = 30;

    /// <summary>Bounded so a wedged ScreenCaptureKit cannot park the caller indefinitely.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    private readonly ScreenCaptureKitFrameSink _sink;
    private readonly GCHandle _sinkHandle;
    private readonly uint _displayId;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private nint _capture;
    private bool _disposed;
    private bool _faulted;
    private bool _pendingCreateOwnsSink;
    private CursorPosition _lastCursorPosition;
    private bool _hasCursorPosition;

    public ScreenCaptureKitCapturer(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        Display = display;

        if (!CoreGraphicsDisplays.TryParse(display.Id, out var displayId))
        {
            throw new ArgumentException($"Display id '{display.Id}' is not a CoreGraphics display id.", nameof(display));
        }

        // The frames should come back at the display's backing size, not its point size: on a Retina panel those
        // differ by the scale factor, and capturing at the point size would silently halve the resolution.
        var (pixelWidth, pixelHeight) = CoreGraphicsDisplays.PixelSize(displayId);

        // CoreGraphics reports no mode for a display that is gone, and PixelSize falls back to the pixel-dimension
        // queries, which answer zero. Catching it here turns what would otherwise be the helper's generic
        // "the capture size must be positive" into something that names the display that is missing.
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ScreenCaptureUnavailableException(
                $"Display {display.Id} is not available: CoreGraphics reports no current mode for it.");
        }

        _displayId = displayId;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;

        _sink = new ScreenCaptureKitFrameSink();
        _sinkHandle = GCHandle.Alloc(_sink);

        try
        {
            OpenCapture(ownsSinkOnTimeout: true);
        }
        catch
        {
            // A timeout on the construction path leaves the late continuation owning the sink; every other
            // failure still has it here. Reset must not take it down: the object stays alive and retries.
            if (!_pendingCreateOwnsSink)
            {
                _sinkHandle.Free();
                _sink.Dispose();
            }

            throw;
        }
    }

    public DisplayInfo Display { get; }

    /// <summary>
    /// Whether ScreenCaptureKit stopped the stream on its own. Without this the capture loop had no way to tell a
    /// static screen from a dead stream and never rebuilt the capturer.
    /// </summary>
    public bool IsFaulted
    {
        get
        {
            if (_faulted)
            {
                // The last rebuild failed. The stream is gone and the sink holds nothing; reporting a fault is
                // what keeps the capture loop retrying instead of waiting for frames from a capture that is not
                // running.
                return true;
            }

            var capture = _capture;
            if (capture == 0)
            {
                return false;
            }

            unsafe
            {
                return MacNativeInterop.rd_capture_stopped(capture, null, 0) != 0;
            }
        }
    }

    /// <summary>Throws the stopped stream away and starts a fresh one on the same sink.</summary>
    /// <remarks>
    /// ScreenCaptureKit's stream cannot be resumed after it stops, so retrying on the same object is not enough:
    /// the native capture is destroyed and created again. A failed rebuild leaves the object marked faulted, and the
    /// capture loop tries again at its recovery interval rather than tearing the session down.
    /// </remarks>
    public void Reset()
    {
        if (_disposed)
        {
            return;
        }

        if (_capture != 0)
        {
            MacNativeInterop.rd_capture_stop(_capture);
            MacNativeInterop.rd_capture_destroy(_capture);
            _capture = 0;
        }

        try
        {
            OpenCapture(ownsSinkOnTimeout: false);
            _faulted = false;
        }
#pragma warning disable CA1031 // A rebuild that fails must leave the capturer retryable, not end the session.
        catch (Exception)
#pragma warning restore CA1031
        {
            _faulted = true;
        }
    }

    public event EventHandler<CursorShape>? CursorChanged;

    public event EventHandler<CursorPosition>? CursorMoved;

    public async ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Read the pointer first so a frame that arrives immediately still gets this frame's cursor state, and
        // again after the wait so a stationary screen still reports a moving pointer.
        RaiseCursorEvents();

        if (timeout <= TimeSpan.Zero)
        {
            RaiseCursorEvents();
            return _sink.TryTake();
        }

        try
        {
            if (!await _sink.WaitForFrameAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        RaiseCursorEvents();
        return _sink.TryTake();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        if (_capture != 0)
        {
            // stop before destroy: destroy also stops, but doing it explicitly means the native queue is quiet
            // before the sink's buffers are considered released, which is what keeps a late frame from writing
            // into pooled memory that has already gone back to the pool.
            MacNativeInterop.rd_capture_stop(_capture);
            MacNativeInterop.rd_capture_destroy(_capture);
            _capture = 0;
        }

        if (_sinkHandle.IsAllocated)
        {
            _sinkHandle.Free();
        }

        _sink.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates and starts one native capture. The caller owns the failure path's cleanup of the sink.</summary>
    /// <param name="ownsSinkOnTimeout">
    /// True during construction, where a late helper answer must release the sink because the object will never be
    /// handed to a caller; false during <see cref="Reset"/>, where the sink outlives the failed attempt.
    /// </param>
    private void OpenCapture(bool ownsSinkOnTimeout)
    {
        _pendingCreateOwnsSink = false;

        // Not on the calling thread: rd_capture_create blocks while ScreenCaptureKit answers its asynchronous
        // shareable-content request, and a caller that happens to be the UI thread must not be parked by it.
        var creation = Task.Run(() => CreateNative(_displayId, _pixelWidth, _pixelHeight));
        try
        {
            _capture = creation.WaitAsync(StartupTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            _pendingCreateOwnsSink = ownsSinkOnTimeout;
            _ = creation.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        MacNativeInterop.rd_capture_destroy(completed.Result);
                    }
                    else
                    {
                        // The caller has already been told the capture timed out. Observe the late failure
                        // instead of letting it resurface as an unobserved task exception.
                        _ = completed.Exception;
                    }

                    if (ownsSinkOnTimeout)
                    {
                        _sinkHandle.Free();
                        _sink.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            throw new ScreenCaptureUnavailableException(
                $"ScreenCaptureKit did not answer within {StartupTimeout.TotalSeconds:0} seconds.");
        }

        unsafe
        {
            var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
            var status = MacNativeInterop.rd_capture_start(_capture, error, MacNativeInterop.ErrorBufferSize);
            if (status != MacNativeInterop.Ok)
            {
                MacNativeInterop.rd_capture_destroy(_capture);
                _capture = 0;
                throw new ScreenCaptureUnavailableException(DescribeFailure(status, error));
            }
        }
    }

    private unsafe nint CreateNative(uint displayId, int pixelWidth, int pixelHeight)
    {
        var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
        var status = MacNativeInterop.rd_capture_create(
            displayId,
            pixelWidth,
            pixelHeight,
            FramesPerSecond,
            MacNativeInterop.FrameCallback,
            GCHandle.ToIntPtr(_sinkHandle),
            out var capture,
            error,
            MacNativeInterop.ErrorBufferSize);

        if (status != MacNativeInterop.Ok)
        {
            throw new ScreenCaptureUnavailableException(DescribeFailure(status, error));
        }

        return capture;
    }

    /// <summary>Prefers the helper's own message, which names the specific failure, over the generic status text.</summary>
    private static unsafe string DescribeFailure(int status, byte* error)
    {
        var detail = MacNativeInterop.ReadError(error, MacNativeInterop.ErrorBufferSize);
        return detail.Length > 0
            ? $"{MacNativeInterop.DescribeStatus(status)} ({detail})"
            : MacNativeInterop.DescribeStatus(status);
    }

    /// <summary>
    /// Reads the pointer and raises whichever of the two events the read implies.
    /// </summary>
    /// <remarks>
    /// The position is reported as the top-left of the cursor image in the captured display's pixels, which is what
    /// <see cref="CursorPosition"/> documents — so the hot spot is subtracted after the scale conversion, not
    /// before. Reading the pointer is cheap (two CoreGraphics calls and, when the shape is unchanged, no pixel
    /// work) but not free, so the snapshot is taken once and both events are derived from it.
    /// </remarks>
    private unsafe void RaiseCursorEvents()
    {
        RdCursorSnapshot snapshot;
        if (MacNativeInterop.rd_cursor_read(&snapshot) != MacNativeInterop.Ok)
        {
            return;
        }

        if (snapshot.Changed != 0 && snapshot.Bgra is not null && CursorChanged is { } shapeChanged)
        {
            var pixels = new byte[snapshot.Width * snapshot.Height * 4];
            Marshal.Copy((nint)snapshot.Bgra, pixels, 0, pixels.Length);
            shapeChanged(this, new CursorShape(
                snapshot.Width,
                snapshot.Height,
                snapshot.HotspotX,
                snapshot.HotspotY,
                pixels));
        }

        var position = ToDisplayPosition(Display, snapshot);
        if (CursorMoved is { } cursorMoved && (!_hasCursorPosition || position != _lastCursorPosition))
        {
            _lastCursorPosition = position;
            _hasCursorPosition = true;
            cursorMoved(this, position);
        }
    }

    /// <summary>
    /// Converts the pointer's global position in points into pixels of a display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one place the three coordinate spaces meet, so it is worth naming them: CoreGraphics reports
    /// displays and the pointer in <em>points</em> with the origin at the main display's top-left; a captured frame
    /// is in <em>pixels</em>, which on a Retina panel is <see cref="DisplayInfo.Scale"/> times as many; and the
    /// protocol's pointer input is <em>normalized 0..1</em>. Doing the conversion here means nothing downstream has
    /// to know the difference.
    /// </para>
    /// <para>
    /// The hot spot is subtracted after scaling, not before, because <see cref="CursorPosition"/> is documented as
    /// the top-left of the cursor <em>image</em> in pixels — and the hot spot is already a pixel offset into that
    /// image.
    /// </para>
    /// </remarks>
    internal static CursorPosition ToDisplayPosition(DisplayInfo display, in RdCursorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(display);
        var scale = display.Scale > 0 ? display.Scale : 1.0;
        var x = ((snapshot.X - display.Bounds.X) * scale) - snapshot.HotspotX;
        var y = ((snapshot.Y - display.Bounds.Y) * scale) - snapshot.HotspotY;
        return new CursorPosition((int)Math.Round(x), (int)Math.Round(y), snapshot.Visible != 0);
    }
}

/// <summary>
/// Thrown when the display cannot be captured — most often because Screen Recording has not been granted.
/// </summary>
/// <remarks>
/// A dedicated type because the caller's response is specific: this is the one capture failure that a user can fix,
/// and it needs to open the right pane of System Settings rather than retry. See
/// <see cref="TccGrants"/> for the check to run first.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class ScreenCaptureUnavailableException : Exception
{
    public ScreenCaptureUnavailableException(string message)
        : base(message)
    {
    }
}
