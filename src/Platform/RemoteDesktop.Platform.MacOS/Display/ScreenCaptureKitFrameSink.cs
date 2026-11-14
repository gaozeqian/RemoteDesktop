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
    private readonly LatestFrameSink<MemoryVideoFrame> _frames = new();

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

        // A write into a completed sink is refused; the frame has to be released by hand in that case.
        if (!_frames.TryWrite(delivered))
        {
            delivered.Dispose();
        }
    }

    /// <summary>Waits for a frame to be delivered, or gives up after <paramref name="timeout"/>.</summary>
    /// <returns><c>true</c> when a frame is waiting to be taken; <c>false</c> on timeout.</returns>
    /// <remarks>
    /// The result of the wait is the answer: a timeout is reported as <c>false</c>, not thrown, so discarding it
    /// would turn every timeout into a reported frame — and the pipeline would then see an empty slot where it
    /// expected a picture.
    /// </remarks>
    public Task<bool> WaitForFrameAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _frames.WaitAsync(timeout, cancellationToken);

    /// <summary>Takes the waiting frame, or returns <c>null</c> when there is none.</summary>
    public VideoFrame? TryTake() => _frames.TryTake(out var frame) ? frame : null;

    /// <summary>Drops a frame left over from a stream that has been replaced.</summary>
    public void Clear() => _frames.Clear();

    public void Dispose() => _frames.Dispose();

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
