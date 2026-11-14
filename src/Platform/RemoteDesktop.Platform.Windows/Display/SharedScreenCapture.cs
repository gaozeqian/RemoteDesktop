using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Platform.Windows.Display;

/// <summary>
/// One Desktop Duplication interface per display, shared by every session watching it.
/// </summary>
/// <remarks>
/// <para>
/// Windows allows a process only one <c>IDXGIOutput1::DuplicateOutput</c> per output. A second call is refused with
/// <c>E_INVALIDARG</c>, so a host with two controllers used to lose the second session at its first frame. The
/// duplication and its capture loop therefore live here, once per display, and sessions subscribe to the newest
/// picture instead of each opening their own.
/// </para>
/// <para>
/// The loop pauses shortly after the last subscriber leaves and resumes for the next one. When it stops, the picture
/// it leaves behind and the duplication under it may both be seconds old — the desktop can have changed a lot while
/// nobody was reading — so the next subscriber gets a rebuilt backend rather than that picture. Handing it over was
/// why disconnecting and reconnecting showed exactly the frame that had been frozen before.
/// </para>
/// <para>
/// A published frame is immutable and reference counted, so subscribers share one copy. The loop recycles buffers
/// through a small pool; a slow subscriber holds its own frame and cannot make the next capture overwrite it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class SharedDisplayCapture : IAsyncDisposable
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FaultLogInterval = TimeSpan.FromSeconds(10);

    /// <summary>A picture older than this is not handed to a subscriber that has just arrived.</summary>
    public static readonly TimeSpan MaxStaleness = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often a newly arriving subscriber may force a rebuild of a running share.
    /// </summary>
    /// <remarks>
    /// One rebuild gives every viewer that joins around the same time a fresh frame. Without the throttle, a burst of
    /// controllers on a static desktop would recreate the duplication for each of them and interrupt the sessions
    /// that were already watching.
    /// </remarks>
    private static readonly TimeSpan SubscriberResetInterval = TimeSpan.FromSeconds(5);

    private readonly Func<DisplayInfo, IScreenCapturer> _createCapturer;
    private readonly ILogger _logger;
    private readonly TimeSpan _idleGrace;
    // Monitor.Wait / Pulse, which a System.Threading.Lock does not offer.
    private readonly object _gate = new();
    private readonly Stack<byte[]> _pool = new();

    private IScreenCapturer? _capturer;
    private Task? _pump;
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _idle;
    private int _subscribers;
    private bool _disposed;
    private bool _reset;
    private bool _faulted;
    private long _lastFaultLog;
    private long _lastSubscriberReset;

    private SharedFrame? _latest;
    private long _version;
    private CursorShape? _cursorShape;
    private long _cursorShapeVersion;
    private CursorPosition? _cursorPosition;
    private long _cursorPositionVersion;

    /// <exception cref="Exception">The display cannot be duplicated; the caller reports it as before.</exception>
    public SharedDisplayCapture(DisplayInfo display, Func<DisplayInfo, IScreenCapturer> createCapturer, ILogger logger, TimeSpan? idleGrace = null)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(createCapturer);
        ArgumentNullException.ThrowIfNull(logger);

        Display = display;
        _logger = logger;
        _idleGrace = idleGrace ?? IdleGrace;
        _createCapturer = createCapturer;

        // Created before the loop starts: a display that cannot be duplicated must fail the session that asked for
        // it, exactly as the per-session capturer did, rather than silently leave a black picture.
        _capturer = createCapturer(display);
        _capturer.CursorChanged += OnCursorShape;
        _capturer.CursorMoved += OnCursorPosition;
    }

    public DisplayInfo Display { get; }

    /// <summary>
    /// Whether this share is currently unable to deliver pictures: the capturer reported a fault, rebuilding it
    /// failed, or the loop is not running at all. A static screen is not a fault — only the capturer can tell the
    /// two apart, and callers must not infer a fault from a run of empty frames.
    /// </summary>
    public bool IsFaulted
    {
        get
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return true;
                }

                return _faulted || _subscribers > 0 && (_pump is null || _pump.IsCompleted);
            }
        }
    }

    /// <summary>Adds a reader, starting the capture loop if it is idle.</summary>
    public SharedScreenCapturer Subscribe()
    {
        while (true)
        {
            Task? stopping = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_pump is { IsCompleted: false } && _stop is { IsCancellationRequested: true })
                {
                    // A new subscriber arrived while the idle loop is winding down. Let it finish before the next
                    // loop touches the capturer: two capture loops would both call AcquireNextFrame.
                    stopping = _pump;
                }
                else
                {
                    _subscribers++;
                    _idle?.Cancel();
                    if (_pump is null || _pump.IsCompleted)
                    {
                        // The loop is starting again after an idle gap; the loop itself marked the backend for a
                        // rebuild when it went away, so nothing from before the gap is replayed here.
                        StartLoopLocked();
                    }
                    else if (_latest is { } latest
                             && Stopwatch.GetElapsedTime(latest.PublishedAt) >= MaxStaleness
                             && (_lastSubscriberReset == 0
                                 || Stopwatch.GetElapsedTime(_lastSubscriberReset) >= SubscriberResetInterval))
                    {
                        // The loop is already running, but its only picture is too old for a subscriber that just
                        // arrived (see MaxStaleness), and a static desktop will not produce a new one. Rebuild the
                        // backend so this arrival gets the desktop as it is now. Throttled, because a burst of
                        // viewers must not make the sessions already watching pay for a rebuild each time.
                        _lastSubscriberReset = Stopwatch.GetTimestamp();
                        _reset = true;
                    }

                    return new SharedScreenCapturer(this);
                }
            }

            try
            {
                stopping.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Stops the loop and releases the duplication. Called when the process exits, not per session.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? pump;
        CancellationTokenSource? stop;
        CancellationTokenSource? idle;
        IScreenCapturer? capturer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _subscribers = 0;
            pump = _pump;
            stop = _stop;
            idle = _idle;
            capturer = _capturer;
            _capturer = null;

            // Cleared as well as cancelled: a session disposed after the container tore the share down must not
            // reach a disposed CancellationTokenSource in Unsubscribe.
            _pump = null;
            _stop = null;
            _idle = null;
            stop?.Cancel();
            idle?.Cancel();
        }

        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        stop?.Dispose();
        idle?.Dispose();
        if (capturer is not null)
        {
            capturer.CursorChanged -= OnCursorShape;
            capturer.CursorMoved -= OnCursorPosition;
            await capturer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Unsubscribe()
    {
        lock (_gate)
        {
            if (_disposed || _subscribers == 0)
            {
                return;
            }

            _subscribers--;
            if (_subscribers == 0)
            {
                _idle?.Cancel();
                _idle?.Dispose();
                _idle = new CancellationTokenSource();
                _ = StopWhenIdleAsync(_idle.Token);
            }
        }
    }

    /// <summary>
    /// Asks the loop to throw the capturer away and build a new one. Used when the backend reports itself broken:
    /// the duplication, the device, or the capture object itself cannot be revived by calling capture again.
    /// </summary>
    public void RequestReset()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _reset = true;
            if (_subscribers > 0 && (_pump is null || _pump.IsCompleted))
            {
                // The loop died holding the only way back to a picture. Restart it rather than leave every
                // subscriber waiting on a share that will never publish again.
                DropLatestLocked();
                StartLoopLocked();
            }
        }
    }

    /// <summary>Waits until a picture newer than <paramref name="lastVersion"/> is available (or the timeout passes).</summary>
    public bool WaitForFrame(long lastVersion, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + ToTimestampTicks(timeout);
        lock (_gate)
        {
            // The picture, not just its version: a version whose picture was dropped (when the last subscriber
            // left) must not report "a new frame is ready" and then hand back nothing.
            while (_latest is null || _version <= lastVersion)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    return false;
                }

                var milliseconds = (int)Math.Min(int.MaxValue, Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency));
                Monitor.Wait(_gate, milliseconds);
            }

            return true;
        }
    }

    /// <summary>Takes a reference to the newest frame when it is newer than <paramref name="lastVersion"/>.</summary>
    public SharedFrame? TakeFrame(long lastVersion)
    {
        lock (_gate)
        {
            if (_version <= lastVersion || _latest is null)
            {
                return null;
            }

            _latest.AddReference();
            return _latest;
        }
    }

    /// <summary>Hands over the cursor updates that arrived since the given versions.</summary>
    public void TakeCursorUpdates(ref long shapeVersion, ref long positionVersion, out CursorShape? shape, out CursorPosition? position)
    {
        lock (_gate)
        {
            shape = _cursorShapeVersion != shapeVersion ? _cursorShape : null;
            position = _cursorPositionVersion != positionVersion ? _cursorPosition : null;
            shapeVersion = _cursorShapeVersion;
            positionVersion = _cursorPositionVersion;
        }
    }

    private static long ToTimestampTicks(TimeSpan timeout) =>
        timeout <= TimeSpan.Zero ? 0 : (long)(timeout.TotalSeconds * Stopwatch.Frequency);

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartLoopLocked()
    {
        _stop?.Dispose();
        var stop = new CancellationTokenSource();

        // The local, not the field: DisposeAsync clears the field, and the task may start after it.
        _stop = stop;
        _pump = Task.Run(() => PumpAsync(stop.Token));
    }

    private async Task StopWhenIdleAsync(CancellationToken idle)
    {
        try
        {
            await Task.Delay(_idleGrace, idle).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (_subscribers == 0)
            {
                _stop?.Cancel();
            }
        }
    }

    private void DropLatestLocked()
    {
        var previous = _latest;
        _latest = null;
        previous?.Release();
    }

    private bool TakeReset()
    {
        lock (_gate)
        {
            if (!_reset)
            {
                return false;
            }

            _reset = false;
            return true;
        }
    }

    private async Task PumpAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (TakeReset())
                {
                    await RecreateAsync(stop).ConfigureAwait(false);
                    continue;
                }

                IScreenCapturer? capturer;
                lock (_gate)
                {
                    capturer = _capturer;
                }

                if (capturer is null)
                {
                    ReportFault(null);
                    await DelayAsync(RetryInterval, stop).ConfigureAwait(false);
                    continue;
                }

                VideoFrame? frame;
                try
                {
                    frame = await capturer.CaptureAsync(CaptureTimeout, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
#pragma warning disable CA1031 // Whatever the backend threw, this loop is the only thing between the failure and a frozen picture.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    // Ending the loop here is what used to freeze every controller of this display until the host
                    // process restarted: nobody else would ever start it again.
                    ReportFault(ex);
                    await DelayAsync(RetryInterval, stop).ConfigureAwait(false);
                    continue;
                }

                if (frame is null)
                {
                    if (capturer.IsFaulted)
                    {
                        ReportFault(null);
                    }

                    continue;
                }

                // A frame means the capturer works again. The flag is cleared before the frame is handed out: a
                // subscriber that has just received a picture must not be able to read IsFaulted as true because this
                // thread has not yet reached the next line.
                lock (_gate)
                {
                    _faulted = false;
                }

                using (frame)
                {
                    Publish(frame);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                // The loop is going away: whatever it left behind predates the next subscriber and must not be
                // replayed, and the backend held by the share is exactly as old.
                _reset = true;
                if (_subscribers == 0)
                {
                    DropLatestLocked();
                }
            }
        }
    }

    /// <summary>Replaces the capturer with a fresh one, so a lost duplication or device gets rebuilt from scratch.</summary>
    private async Task RecreateAsync(CancellationToken stop)
    {
        IScreenCapturer? old;
        lock (_gate)
        {
            old = _capturer;
            _capturer = null;
            DropLatestLocked();
        }

        if (old is not null)
        {
            old.CursorChanged -= OnCursorShape;
            old.CursorMoved -= OnCursorPosition;
            try
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDisposeFailed(_logger, Display.Id, ex);
            }
        }

        try
        {
            var fresh = _createCapturer(Display);
            fresh.CursorChanged += OnCursorShape;
            fresh.CursorMoved += OnCursorPosition;
            lock (_gate)
            {
                _capturer = fresh;
            }

            LogCapturerRebuilt(_logger, Display.Id);
        }
#pragma warning disable CA1031 // A secure desktop or an unplugged monitor refuses every backend; keep retrying quietly.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportFault(ex);
            await DelayAsync(RetryInterval, stop).ConfigureAwait(false);
        }
    }

    private void ReportFault(Exception? error)
    {
        bool log;
        lock (_gate)
        {
            _faulted = true;
            _reset = true;
            log = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFaultLog)) >= FaultLogInterval;
            if (log)
            {
                Volatile.Write(ref _lastFaultLog, Stopwatch.GetTimestamp());
            }
        }

        if (!log)
        {
            return;
        }

        if (error is null)
        {
            LogCapturerFaulted(_logger, Display.Id);
        }
        else
        {
            LogCaptureFailed(_logger, Display.Id, error);
        }
    }

    private void Publish(VideoFrame frame)
    {
        var pixels = frame.GetPixels(out var stride);
        var length = stride * frame.Height;

        byte[] buffer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Also published with no subscriber left: the copy keeps the newest picture current during the idle
            // grace, so a controller that reconnects inside it gets the desktop as it is now. Dropping it there
            // consumed the change — DXGI only reports what is new since the last frame — and the picture before
            // the change became permanent.
            buffer = _pool.Count > 0 ? _pool.Pop() : [];
        }

        if (buffer.Length < length)
        {
            buffer = new byte[length];
        }

        // The copy is the expensive part and must not hold _gate: WaitForFrame, TakeFrame and the cursor readers all
        // take the same lock, and a 4K memcpy here would serialize them behind the capture loop.
        pixels[..length].CopyTo(buffer);

        lock (_gate)
        {
            if (_disposed)
            {
                // The share went away while the copy ran; the buffer is dropped rather than returned to a dead pool.
                return;
            }

            var next = new SharedFrame(
                buffer, frame.Width, frame.Height, stride, frame.Timestamp, ++_version, Stopwatch.GetTimestamp(), ReturnBuffer);
            var previous = _latest;
            _latest = next;
            previous?.Release();
            Monitor.PulseAll(_gate);
        }
    }

    private void ReturnBuffer(byte[] buffer)
    {
        lock (_gate)
        {
            _pool.Push(buffer);
        }
    }

    private void OnCursorShape(object? sender, CursorShape shape)
    {
        lock (_gate)
        {
            _cursorShape = shape;
            _cursorShapeVersion++;
            Monitor.PulseAll(_gate);
        }
    }

    private void OnCursorPosition(object? sender, CursorPosition position)
    {
        lock (_gate)
        {
            _cursorPosition = position;
            _cursorPositionVersion++;
            Monitor.PulseAll(_gate);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Desktop capture failed on {Display}; rebuilding it.")]
    private static partial void LogCaptureFailed(ILogger logger, string display, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Desktop capture on {Display} reported a fault; rebuilding it.")]
    private static partial void LogCapturerFaulted(ILogger logger, string display);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rebuilt desktop capture for {Display}.")]
    private static partial void LogCapturerRebuilt(ILogger logger, string display);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release the previous capture of {Display}.")]
    private static partial void LogDisposeFailed(ILogger logger, string display, Exception exception);
}

/// <summary>One session's subscription to a <see cref="SharedDisplayCapture"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SharedScreenCapturer(SharedDisplayCapture capture) : IScreenCapturer
{
    private readonly long _subscribedAt = Stopwatch.GetTimestamp();
    private long _version;
    private long _shapeVersion;
    private long _positionVersion;
    private bool _frameOut;
    private bool _awaitingFirstFrame = true;
    private bool _disposed;

    public DisplayInfo Display => capture.Display;

    public bool IsFaulted => capture.IsFaulted;

    /// <summary>Asks the share to rebuild what it captures with; the subscription itself stays valid.</summary>
    public void Reset() => capture.RequestReset();

    public event EventHandler<CursorShape>? CursorChanged;

    public event EventHandler<CursorPosition>? CursorMoved;

    public ValueTask<VideoFrame?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOut)
        {
            throw new InvalidOperationException("Dispose the previous frame before capturing the next one.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var available = capture.WaitForFrame(_version, timeout, cancellationToken);
        RaiseCursorEvents();

        var frame = available ? capture.TakeFrame(_version) : null;
        if (frame is null)
        {
            return ValueTask.FromResult<VideoFrame?>(null);
        }

        _version = frame.Version;
        if (_awaitingFirstFrame && Stopwatch.GetElapsedTime(frame.PublishedAt, _subscribedAt) > SharedDisplayCapture.MaxStaleness)
        {
            frame.Release();
            return ValueTask.FromResult<VideoFrame?>(null);
        }

        _awaitingFirstFrame = false;
        _frameOut = true;
        return ValueTask.FromResult<VideoFrame?>(new SharedVideoFrame(frame, () => _frameOut = false)
        {
            Width = frame.Width,
            Height = frame.Height,
            Format = PixelFormat.Bgra32,
            Timestamp = frame.Timestamp,
        });
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        capture.Unsubscribe();
        return ValueTask.CompletedTask;
    }

    private void RaiseCursorEvents()
    {
        capture.TakeCursorUpdates(ref _shapeVersion, ref _positionVersion, out var shape, out var position);
        if (shape is not null)
        {
            CursorChanged?.Invoke(this, shape);
        }

        if (position is { } moved)
        {
            CursorMoved?.Invoke(this, moved);
        }
    }
}

/// <summary>An immutable picture published to every subscriber of a display.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SharedFrame(byte[] buffer, int width, int height, int stride, TimeSpan timestamp, long version, long publishedAt, Action<byte[]> release)
{
    private int _references = 1; // the capture loop's own reference, held as the latest frame

    public byte[] Buffer { get; } = buffer;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride { get; } = stride;

    public TimeSpan Timestamp { get; } = timestamp;

    public long Version { get; } = version;

    /// <summary><see cref="Stopwatch"/> timestamp of the moment this picture was captured.</summary>
    public long PublishedAt { get; } = publishedAt;

    public void AddReference() => Interlocked.Increment(ref _references);

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            release(Buffer);
        }
    }
}

/// <summary>The <see cref="VideoFrame"/> handed to one session; releasing it returns its reference to the share.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SharedVideoFrame(SharedFrame frame, Action released) : VideoFrame
{
    private int _disposed;

    public override ReadOnlySpan<byte> GetPixels(out int stride)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        stride = frame.Stride;
        return frame.Buffer.AsSpan(0, frame.Stride * frame.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            frame.Release();
            released();
        }

        base.Dispose(disposing);
    }
}
