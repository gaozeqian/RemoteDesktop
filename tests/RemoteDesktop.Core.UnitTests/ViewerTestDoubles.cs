using System.IO.Pipes;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>Hands out <see cref="FakeDecoder"/>s and counts what is asked of them.</summary>
public sealed class FakeDecoderFactory : IVideoDecoderFactory
{
    private int _created;
    private int _failedCreates;
    private int _decodes;

    public string Name => "fake";

    public bool IsHardware => false;

    public int Priority => 0;

    public bool FailNextCreate { get; set; }

    /// <summary>When set, decoders answer every unit with a picture instead of nothing.</summary>
    public bool ProducePictures { get; set; }

    public List<FakeDecoder> Decoders { get; } = [];

    /// <summary>The size each decoder (in the order of <see cref="Decoders"/>) was created for.</summary>
    public List<(int Width, int Height)> Sizes { get; } = [];

    public int Created => Volatile.Read(ref _created);

    public int FailedCreates => Volatile.Read(ref _failedCreates);

    /// <summary>Units that reached any decoder.</summary>
    public int Decodes => Volatile.Read(ref _decodes);

    public bool IsSupported(VideoCodec codec) => true;

    public IVideoDecoder Create(VideoCodec codec, int width, int height)
    {
        if (FailNextCreate)
        {
            FailNextCreate = false;
            Interlocked.Increment(ref _failedCreates);
            throw new InvalidOperationException("No decoder left.");
        }

        var decoder = new FakeDecoder(() => Interlocked.Increment(ref _decodes), () => ProducePictures);
        lock (Decoders)
        {
            Decoders.Add(decoder);
            Sizes.Add((width, height));
        }

        Interlocked.Increment(ref _created);
        return decoder;
    }
}

/// <summary>By default a decoder that has lost sync for good: it takes everything and shows nothing.</summary>
public sealed class FakeDecoder(Action onDecode, Func<bool> producePicture) : IVideoDecoder
{
    private int _decodes;
    private volatile bool _disposed;

    public string Name => "fake";

    public bool Disposed => _disposed;

    /// <summary>Units this decoder was given.</summary>
    public int Decodes => Volatile.Read(ref _decodes);

    public VideoFrame? Decode(EncodedVideoFrame frame)
    {
        Interlocked.Increment(ref _decodes);
        onDecode();
        return producePicture() ? new MemoryVideoFrame(64, 64, PixelFormat.Nv12, TimeSpan.Zero) : null;
    }

    public void Dispose() => _disposed = true;
}

public sealed class FakeConnection(Stream stream) : IPeerConnection
{
    public DeviceId RemoteDevice => new(111_111_111);

    public ConnectionPath Path => ConnectionPath.Direct;

    public string? RelaySessionId => null;

    public string LocalFingerprint => "local";

    public string RemoteFingerprint => "remote";

    public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IChannel>(new TestChannel(type, stream));

    public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IChannel?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Two ends of a buffered pipe: the viewer gets <c>Server</c>, the test plays the host on <c>Client</c>.</summary>
public static class TestPipe
{
    public static async Task<(Stream Server, Stream Client)> ConnectAsync(CancellationToken cancellationToken)
    {
        var name = $"rd-unit-{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(cancellationToken), client.ConnectAsync(cancellationToken));
        return (server, client);
    }
}

/// <summary>
/// A clock (both the wall time and the monotonic timestamp) that stands still until the test moves it, and fires the timers (<c>Task.Delay</c> with a
/// <see cref="TimeProvider"/> is one) that fall due. <see cref="PendingTimers"/> and <see cref="TimersCreated"/> let a
/// test tell when the code under test has gone back to waiting.
/// </summary>
public sealed class SteppedTime : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<SteppedTimer> _timers = [];
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private DateTimeOffset _now = Start;
    private int _created;

    /// <summary>Timers that are armed and have not fired or been disposed.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    /// <summary>How many timers were ever created.</summary>
    public int TimersCreated => Volatile.Read(ref _created);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    /// <summary>The same stand-still time as <see cref="GetUtcNow"/>, in ticks since the clock was created.</summary>
    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return (_now - Start).Ticks;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SteppedTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        Interlocked.Increment(ref _created);
        return timer;
    }

    /// <summary>Moves the clock and runs every timer that is now due (on the calling thread).</summary>
    public void Advance(TimeSpan by)
    {
        List<SteppedTimer> due;
        lock (_lock)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due is { } at && at <= _now)];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(SteppedTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class SteppedTimer(SteppedTime owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._lock)
            {
                Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                Due = null;
            }

            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
