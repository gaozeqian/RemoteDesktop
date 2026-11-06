using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol.Control;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// An established session as seen by the controller. Keeps the control channel alive with pings, which also
/// measure the round-trip time shown in the UI and reported in telemetry. Bitrate adaptation uses the encoder's own
/// send timing, not this number.
/// </summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);

    private readonly IChannel _control;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task _loops = Task.CompletedTask;
    private readonly object _closedGate = new();
    private EventHandler? _closedHandlers;
    private bool _closed;
    private int _disposed;
    private volatile InputAccessStatus? _inputAccess;
    private ulong _inputAccessSequence;

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

    /// <summary>
    /// Raised once when the session ends for any reason (peer closed, network lost, disposed). A handler added after
    /// the session has already ended is called immediately: the loops start before the caller can subscribe, so
    /// without this a peer that closes right away would never be reported.
    /// </summary>
    public event EventHandler? Closed
    {
        add
        {
            lock (_closedGate)
            {
                if (!_closed)
                {
                    _closedHandlers += value;
                    return;
                }
            }

            value?.Invoke(this, EventArgs.Empty);
        }

        remove
        {
            lock (_closedGate)
            {
                _closedHandlers -= value;
            }
        }
    }

    /// <summary>Raised when the host changed what this session may do (e.g. switched it to watch-only); see <see cref="SessionContext.Grant"/>.</summary>
    public event EventHandler? GrantChanged;

    /// <summary>
    /// The host operating system's permission for injecting input, or <c>null</c> until the host says anything.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SessionContext.Grant"/>: the grant is the host's policy, this is whether the desktop
    /// will actually accept the input. A host platform with no separate permission sends nothing, and the controller
    /// must not treat silence as a refusal.
    /// </remarks>
    public InputAccessStatus? InputAccess => _inputAccess;

    /// <summary>Raised when <see cref="InputAccess"/> changes, on the control channel's read loop.</summary>
    public event EventHandler? InputAccessChanged;

    /// <summary>
    /// Asks the host to request the desktop's permission again (the controller's "retry" action).
    /// </summary>
    /// <remarks>Fire-and-forget at this level: the answer arrives as an <see cref="InputAccessChanged"/>.</remarks>
    public async Task RequestInputAccessAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ControlMessages.WriteAsync(
                _control,
                new ControlMessage { RequestInputAccess = new RequestInputAccess() },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

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
                else if (message.BodyCase == ControlMessage.BodyOneofCase.GrantChanged)
                {
                    // The host decides; this only mirrors it so the UI stops offering what is no longer allowed.
                    var granted = ControlMessages.FromWire(message.GrantChanged.GrantedPermissions);
                    Context.Grant = AccessGrant.Create(ControlMessages.FromMessage(message.GrantChanged.Mode), granted, granted);
                    GrantChanged?.Invoke(this, EventArgs.Empty);
                }
                else if (message.BodyCase == ControlMessage.BodyOneofCase.InputAccessChanged)
                {
                    var change = message.InputAccessChanged;

                    // The initial status and a live change can be in flight together when a session starts. Applying
                    // only increasing sequence numbers keeps an older one from overwriting the newer state.
                    if (change.Sequence <= _inputAccessSequence)
                    {
                        continue;
                    }

                    _inputAccessSequence = change.Sequence;
                    _inputAccess = new InputAccessStatus(
                        ControlMessages.FromMessage(change.State),
                        string.IsNullOrEmpty(change.Reason) ? null : change.Reason);
                    InputAccessChanged?.Invoke(this, EventArgs.Empty);
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
        EventHandler? handlers;
        lock (_closedGate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            handlers = _closedHandlers;
            _closedHandlers = null;
        }

#pragma warning disable CA1031 // A subscriber's failure must not turn "the session ended" into a teardown exception.
        try
        {
            handlers?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }
}
