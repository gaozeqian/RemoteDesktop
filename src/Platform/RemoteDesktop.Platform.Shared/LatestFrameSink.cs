namespace RemoteDesktop.Platform.Shared;

/// <summary>
/// Holds at most one frame: a newer frame replaces the waiting one, and a consumer always gets the most recent
/// picture rather than a backlog. The replaced frame is disposed immediately.
/// </summary>
/// <remarks>
/// <para>
/// A live view wants the newest picture; queueing would only add latency and memory. The macOS ScreenCaptureKit and
/// camera sinks implemented this with two slightly different mechanisms — a bounded channel and a semaphore plus a
/// field — and the camera on Windows and Linux does the same with a bounded channel. This is the semaphore shape,
/// which the screen capturer already relied on, generalized over the frame type.
/// </para>
/// <para>
/// One consumer is assumed (a capture enumeration). The producer may be a native callback on another thread, which
/// is why every field is behind the lock and the wait is a semaphore rather than a condition variable.
/// </para>
/// </remarks>
internal sealed class LatestFrameSink<T> : IDisposable
    where T : class, IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private T? _pending;
    private Exception? _completionError;
    private bool _completed;
    private bool _disposed;

    /// <summary>Replaces the waiting frame. Returns false when the sink is completed or disposed; dispose the value then.</summary>
    public bool TryWrite(T value)
    {
        T? replaced;
        lock (_gate)
        {
            if (_disposed || _completed)
            {
                return false;
            }

            replaced = _pending;
            _pending = value;
        }

        replaced?.Dispose();

        // Capped at one: a second release would throw, and a second signal means nothing when only one frame is held.
        if (_available.CurrentCount == 0)
        {
            _available.Release();
        }

        return true;
    }

    /// <summary>Takes the waiting frame, or returns false when there is none.</summary>
    public bool TryTake(out T? value)
    {
        bool signalCompletion;
        lock (_gate)
        {
            value = _pending;
            _pending = null;

            // A sink completed while a frame was waiting: taking the last frame has to wake the next wait so it can
            // report the end (or the error) instead of blocking until the timeout.
            signalCompletion = _completed;
        }

        if (signalCompletion && _available.CurrentCount == 0)
        {
            _available.Release();
        }

        return value is not null;
    }

    /// <summary>
    /// Waits for a frame. Returns false on timeout or after a clean completion; throws the completion error when one
    /// was given, and <see cref="ObjectDisposedException"/> when the sink was disposed while waiting.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!await _available.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            lock (_gate)
            {
                if (_pending is not null)
                {
                    return true;
                }

                if (_completed)
                {
                    if (_completionError is { } error)
                    {
                        throw error;
                    }

                    return false;
                }
            }

            // No frame and no completion: the signal was consumed by a concurrent reader or completion. Wait again.
        }
    }

    /// <summary>Drops the waiting frame and its wake-up without completing the sink.</summary>
    /// <remarks>
    /// A capture that is rebuilt keeps its sink, and the picture queued by the dead stream is not a picture of the
    /// new one. Consuming the semaphore too matters: otherwise the next wait would report a frame that Clear has
    /// just released.
    /// </remarks>
    public void Clear()
    {
        T? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.Dispose();

        while (_available.Wait(0))
        {
        }
    }

    /// <summary>Stops accepting frames; the waiting frame, if any, can still be taken before the wait reports the end.</summary>
    public void Complete(Exception? error = null)
    {
        var signal = false;
        lock (_gate)
        {
            if (_completed || _disposed)
            {
                return;
            }

            _completed = true;
            _completionError = error;
            signal = _pending is null;
        }

        if (signal && _available.CurrentCount == 0)
        {
            _available.Release();
        }
    }

    public void Dispose()
    {
        T? pending;
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
}
