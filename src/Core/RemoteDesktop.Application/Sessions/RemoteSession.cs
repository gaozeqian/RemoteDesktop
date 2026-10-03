using RemoteDesktop.Application.Features;
using RemoteDesktop.Protocol.Control;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// An established session as seen by the controller. Keeps the control channel alive with pings, which also
/// measure the round-trip time used for display and bitrate adaptation.
/// </summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);

    private readonly IChannel _control;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task _loops = Task.CompletedTask;
    private int _closed;
    private int _disposed;

    private RemoteSession(SessionContext context, IChannel control, TimeProvider time)
    {
        Context = context;
        _control = control;
        _time = time;
    }

    public SessionContext Context { get; }

    public ConnectionPath Path => Context.Connection.Path;

    /// <summary>Latest measured round-trip time; <c>null</c> until the first pong.</summary>
    public TimeSpan? RoundTripTime { get; private set; }

    public event EventHandler? RoundTripTimeChanged;

    /// <summary>Raised once when the session ends for any reason (peer closed, network lost, disposed).</summary>
    public event EventHandler? Closed;

    internal static RemoteSession Start(SessionContext context, IChannel control, TimeProvider time)
    {
        var session = new RemoteSession(context, control, time);
        session._loops = Task.WhenAll(session.ReadLoopAsync(), session.PingLoopAsync());
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _control.DisposeAsync().ConfigureAwait(false);
        await Context.Connection.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _loops.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        RaiseClosed();
        _stop.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await ControlMessages.ReadAsync(_control, _stop.Token).ConfigureAwait(false) is { } message)
            {
                if (message.BodyCase == ControlMessage.BodyOneofCase.Pong)
                {
                    var sent = message.Pong.Timestamp;
                    RoundTripTime = _time.GetElapsedTime(sent);
                    RoundTripTimeChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            RaiseClosed();
        }
    }

    private async Task PingLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _writeLock.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    var ping = new ControlMessage { Ping = new Ping { Timestamp = _time.GetTimestamp() } };
                    await ControlMessages.WriteAsync(_control, ping, _stop.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }

                await Task.Delay(PingInterval, _time, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
