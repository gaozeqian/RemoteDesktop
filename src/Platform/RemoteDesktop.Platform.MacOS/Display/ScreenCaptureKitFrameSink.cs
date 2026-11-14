using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.MacOS.Interop;
using RemoteDesktop.Platform.Shared;

namespace RemoteDesktop.Platform.MacOS.Display;

/// <summary>
/// Receives frames from the native helper's queue and hands them to <see cref="ScreenCaptureKitCapturer"/>.
/// </summary>
/// <remarks>
/// <para>
/// The helper calls <see cref="Deliver"/> on a serial dispatch queue, so deliveries never overlap with each other;
/// <see cref="WaitForFrameAsync"/> and <see cref="TryTake"/> are called from whatever thread is capturing, so the
/// two sides do need the lock.
/// </para>
/// <para>
/// Only one frame is held. If the consumer is slower than the screen, the newer frame replaces the older one rather
/// than queueing behind it: a live view wants the most recent picture, not a backlog. The replaced frame goes back
/// to the pool immediately.
/// </para>
/// <para>
/// The copy in <see cref="Deliver"/> is not an optimisation that was skipped — it is required. ScreenCaptureKit
/// lends the pixel buffer for the duration of the callback only, so anything the consumer might read later has to
/// be moved into memory this side owns before returning.
/// </para>
/// </remarks>
internal sealed class ScreenCaptureKitFrameSink : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private MemoryVideoFrame? _pending;
    private bool _disposed;

    /// <summary>Called from the helper's queue. Must not throw: it is reached through a reverse P/Invoke.</summary>
    public unsafe void Deliver(RdFrame* frame)
    {
        if (frame is null || frame->Bgra is null || frame->Width < 2 || frame->Height < 2)
        {
            return;
        }

        var width = frame->Width;
        var height = frame->Height;

        MemoryVideoFrame delivered;
        try
        {
            delivered = new MemoryVideoFrame(width, height, PixelFormat.Bgra32, HostTime.ToTimestamp(frame->HostTimeNanoseconds))
            {
                DirtyRects = ReadDirtyRects(frame),
            };
        }
        catch (ArgumentException)
        {
            // A frame with impossible dimensions (odd size for a subsampled format, or a zero) is dropped rather
            // than allowed to escape into Objective-C, where unwinding would be undefined.
            return;
        }

        var destination = delivered.Buffer;
        var sourceRow = frame->Bgra;
        var sourceStride = frame->BytesPerRow;
        var destinationStride = delivered.Stride;

        // The source rows are padded to a hardware alignment that is usually wider than the visible width, so this
        // is a row-by-row copy rather than one memcpy over the whole buffer.
        BgraRowCopy.Copy(sourceRow, sourceStride, destination, destinationStride, height);

        MemoryVideoFrame? replaced;
        lock (_gate)
        {
            if (_disposed)
            {
                delivered.Dispose();
                return;
            }

            replaced = _pending;
            _pending = delivered;
        }

        replaced?.Dispose();

        // Release only when nothing is already signalled: the semaphore is capped at one, so a second release
        // would throw, and a second signal would be meaningless anyway since only one frame is ever held.
        if (_available.CurrentCount == 0)
        {
            _available.Release();
        }
    }

    /// <summary>Waits for a frame to be delivered, or gives up after <paramref name="timeout"/>.</summary>
    /// <returns><c>true</c> when a frame is waiting to be taken; <c>false</c> on timeout.</returns>
    /// <remarks>
    /// The result of the wait is the answer: <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/>
    /// reports a timeout by returning <c>false</c>, not by throwing, so discarding it would turn every timeout into
    /// a reported frame — and the pipeline would then see an empty slot where it expected a picture.
    /// </remarks>
    public async Task<bool> WaitForFrameAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _available.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);

    /// <summary>Takes the waiting frame, or returns <c>null</c> when there is none.</summary>
    public VideoFrame? TryTake()
    {
        lock (_gate)
        {
            var frame = _pending;
            _pending = null;
            return frame;
        }
    }

    public void Dispose()
    {
        MemoryVideoFrame? pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pending;
            _pending = null;
        }

        pending?.Dispose();
        _available.Dispose();
    }

    private static unsafe PixelRect[] ReadDirtyRects(RdFrame* frame)
    {
        if (frame->DirtyRects is null || frame->DirtyCount <= 0)
        {
            // Empty means "assume the whole frame changed", which is the contract's definition and the safe
            // reading: ScreenCaptureKit omits dirty rectangles for the first frame of a stream.
            return [];
        }

        var rects = new PixelRect[frame->DirtyCount];
        for (var i = 0; i < rects.Length; i++)
        {
            var rect = frame->DirtyRects[i];
            rects[i] = new PixelRect(rect.X, rect.Y, rect.Width, rect.Height);
        }

        return rects;
    }
}
