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
/// The loop pauses shortly after the last subscriber leaves and resumes for the next one, but the duplication itself
/// stays open for the life of the process: recreating it only to discover it is still being torn down is the same bug
/// in a narrower window, and the host always expects somebody to connect again.
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

    private readonly IScreenCapturer _capturer;
    private readonly ILogger _logger;
    private readonly TimeSpan _idleGrace;
    // Monitor.Wait / Pulse, which a System.Threading.Lock does not offer.
    private readonly object _gate = new();
    private readonly Stack<byte[]> _pool = new();

    private Task? _pump;
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _idle;
    private int _subscribers;
    private bool _disposed;

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

        // Created before the loop starts: a display that cannot be duplicated must fail the session that asked for
        // it, exactly as the per-session capturer did, rather than silently leave a black picture.
        _capturer = createCapturer(display);
        _capturer.CursorChanged += OnCursorShape;
        _capturer.CursorMoved += OnCursorPosition;
    }

    public DisplayInfo Display { get; }

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
                        _stop?.Dispose();
                        var stop = new CancellationTokenSource();

                        // The local, not the field: DisposeAsync clears the field, and the task may start after it.
                        _stop = stop;
                        _pump = Task.Run(() => PumpAsync(stop.Token));
                    }

                    return new SharedScreenCapturer(this);
                }
            }

            stopping.GetAwaiter().GetResult();
        }
    }

    /// <summary>Stops the loop and releases the duplication. Called when the process exits, not per session.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? pump;
        CancellationTokenSource? stop;
        CancellationTokenSource? idle;
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
        _capturer.CursorChanged -= OnCursorShape;
        _capturer.CursorMoved -= OnCursorPosition;
        await _capturer.DisposeAsync().ConfigureAwait(false);
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

    /// <summary>Waits until a picture newer than <paramref name="lastVersion"/> is available (or the timeout passes).</summary>
    public bool WaitForFrame(long lastVersion, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + ToTimestampTicks(timeout);
        lock (_gate)
        {
            while (_version <= lastVersion)
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

    private async Task PumpAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            VideoFrame? frame;
            try
            {
                frame = await _capturer.CaptureAsync(CaptureTimeout, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException)
            {
                // The capturer retries access loss itself; this is the unexpected kind. Keep the loop alive for the
                // other subscribers rather than ending the display for everyone.
                LogCaptureFailed(_logger, Display.Id, ex);
                try
                {
                    await Task.Delay(RetryInterval, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (frame is null)
            {
                continue;
            }

            using (frame)
            {
                Publish(frame);
            }
        }
    }

    private void Publish(VideoFrame frame)
    {
        var pixels = frame.GetPixels(out var stride);
        var length = stride * frame.Height;
        lock (_gate)
        {
            // Nobody is reading: the copy is wasted, and the old picture is still the newest one for the next
            // subscriber until the capturer delivers a changed one.
            if (_subscribers == 0)
            {
                return;
            }

            var buffer = _pool.Count > 0 ? _pool.Pop() : [];
            if (buffer.Length < length)
            {
                buffer = new byte[length];
            }

            pixels[..length].CopyTo(buffer);
            var next = new SharedFrame(buffer, frame.Width, frame.Height, stride, frame.Timestamp, ++_version, ReturnBuffer);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Desktop capture failed on {Display}; retrying.")]
    private static partial void LogCaptureFailed(ILogger logger, string display, Exception exception);
}

/// <summary>One session's subscription to a <see cref="SharedDisplayCapture"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SharedScreenCapturer(SharedDisplayCapture capture) : IScreenCapturer
{
    private long _version;
    private long _shapeVersion;
    private long _positionVersion;
    private bool _frameOut;
    private bool _disposed;

    public DisplayInfo Display => capture.Display;

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
internal sealed class SharedFrame(byte[] buffer, int width, int height, int stride, TimeSpan timestamp, long version, Action<byte[]> release)
{
    private int _references = 1; // the capture loop's own reference, held as the latest frame

    public byte[] Buffer { get; } = buffer;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride { get; } = stride;

    public TimeSpan Timestamp { get; } = timestamp;

    public long Version { get; } = version;

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
