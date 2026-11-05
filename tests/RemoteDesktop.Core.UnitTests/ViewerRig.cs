using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// A viewer under test plus the host it talks to, played by the other end of a pipe: it sends frames, and counts the
/// key frame requests that come back. The viewer runs on a <see cref="SteppedTime"/>, so its watchdog only ticks when
/// the test says so. What the stall judgement measures is real monotonic time and cannot be stepped from here.
/// </summary>
public abstract class ViewerRig : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _pump = Task.CompletedTask;
    private int _keyFrameRequests;
    private bool _disposed;

    protected ViewerRig(Stream host, FakeDecoderFactory factory, SteppedTime time)
    {
        Host = host;
        Factory = factory;
        Time = time;
    }

    public FakeDecoderFactory Factory { get; }

    public SteppedTime Time { get; }

    public bool Closed { get; protected set; }

    /// <summary>Key frame requests the host has received so far.</summary>
    public int KeyFrameRequests => Volatile.Read(ref _keyFrameRequests);

    /// <summary>Pictures decoded so far.</summary>
    public abstract long FrameNumber { get; }

    /// <summary>The width of the stream the viewer currently shows, or zero before there is one.</summary>
    public abstract int StreamWidth { get; }

    protected Stream Host { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask SendKeyFrameAsync() => SendAsync(isKeyFrame: true);

    public ValueTask SendRawVideoFrameAsync(byte[] payload) =>
        FrameCodec.WriteAsync(Host, new Frame(VideoDataFrame.MessageType, payload), Ct);

    public async ValueTask SendDeltaFramesAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await SendAsync(isKeyFrame: false);
        }
    }

    public abstract ValueTask SendStreamInfoAsync(uint width, uint height);

    /// <summary>Makes the viewer send a key frame request of its own; used to tell the host's side "that was all".</summary>
    public abstract Task AskForKeyFrameAsync();

    public async Task WaitForDecodesAsync(int count) =>
        Assert.True(await WaitUntilAsync(() => Factory.Decodes >= count), $"{count} frame(s) reached a decoder");

    /// <summary>
    /// Waits until the host has received exactly <paramref name="expected"/> key frame requests from the viewer's own
    /// doing. The viewer is asked for one more, and the pipe keeps order, so when that last one arrives everything
    /// before it has been counted. A request that wrongly turned up would only be missed if it were still in flight.
    /// </summary>
    public async Task ExpectKeyFrameRequestsAsync(int expected)
    {
        await AskForKeyFrameAsync();
        Assert.True(await WaitUntilAsync(() => KeyFrameRequests >= expected + 1), "the viewer's last request arrived");
        Assert.Equal(expected + 1, KeyFrameRequests);
        Interlocked.Decrement(ref _keyFrameRequests);
    }

    /// <summary>Moves the viewer's clock and waits until its watchdog has run its pass and gone back to waiting.</summary>
    public async Task TickAsync(TimeSpan by)
    {
        var before = Time.TimersCreated;
        Time.Advance(by);
        Assert.True(await WaitUntilAsync(() => Time.TimersCreated > before), "the watchdog went back to waiting");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisposeViewerAsync();
        await Host.DisposeAsync();
        await _stop.CancelAsync();
        await _pump;
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    public static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }

        return condition();
    }

    protected abstract ValueTask DisposeViewerAsync();

    /// <summary>Reads one request of the viewer; <c>null</c> when the stream is over, else whether it asked for a key frame.</summary>
    protected abstract ValueTask<bool?> ReadRequestAsync(Stream host, CancellationToken cancellationToken);

    /// <summary>Starts counting the viewer's key frame requests; until then they simply sit in the pipe.</summary>
    protected void StartPump() => _pump = Task.Run(PumpAsync, CancellationToken.None);

    private async Task PumpAsync()
    {
        try
        {
            while (await ReadRequestAsync(Host, _stop.Token) is { } isKeyFrameRequest)
            {
                if (isKeyFrameRequest)
                {
                    Interlocked.Increment(ref _keyFrameRequests);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
            // The pipe went away with the test (possibly in the middle of a message).
        }
    }

    private ValueTask SendAsync(bool isKeyFrame) =>
        VideoDataFrame.WriteAsync(Host, new EncodedVideoFrame(new byte[] { 0, 0, 0, 1, 0x65 }, isKeyFrame, TimeSpan.Zero), DateTimeOffset.UtcNow, Ct);
}
