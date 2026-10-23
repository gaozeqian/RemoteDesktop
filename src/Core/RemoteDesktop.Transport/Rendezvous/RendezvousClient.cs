using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Identity;

namespace RemoteDesktop.Transport.Rendezvous;

public sealed partial class RendezvousClient(TransportOptions options, DeviceIdentity identity, ILogger<RendezvousClient> logger)
    : IRendezvousClient, IDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Deliberately not <see cref="TransportOptions.SignalingTimeout"/>: that is a 25-second budget meant for a connect
    /// a person is watching. This one is a background poll — an answer that takes longer is no answer.
    /// </summary>
    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(3);

    private readonly Channel<IncomingConnection> _incoming = Channel.CreateBounded<IncomingConnection>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

    // Unbounded on purpose: an admin command is authoritative and rare, and dropping one is not a retryable loss the
    // way dropping a presence answer is. The only writer is the receive loop, which the server can only feed through
    // the console's own approval flow.
    private readonly Channel<AdminCommand> _admin = Channel.CreateUnbounded<AdminCommand>();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ConnectReady>> _pending = new(StringComparer.Ordinal);

    // A second map rather than a shared one: a presence answer travels an entirely different path from a connect, and
    // keeping it apart leaves the connect path exactly as it was.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PresenceResult>> _pendingPresence = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Heartbeats waiting to go out. Bounded and drop-oldest: telemetry is a sample, not a ledger, and a device with
    /// sixteen sessions must never be able to grow this queue while the connection is down.
    /// </summary>
    private readonly Channel<TelemetryReport> _telemetry = Channel.CreateBounded<TelemetryReport>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// Session-end reports waiting to go out. A separate queue from heartbeats so a burst of samples cannot push out
    /// the one fact that closes a row. A device hosts at most 16 sessions, so the bound is generous.
    /// </summary>
    private readonly Channel<SessionEndedReport> _sessionEnds = Channel.CreateBounded<SessionEndedReport>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });

    private ClientWebSocket? _socket;

    public RendezvousState State { get; private set; }

    public DeviceId? DeviceId { get; private set; }

    public IPEndPoint? ReflectorEndPoint { get; private set; }

    public event EventHandler? StateChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(RendezvousState.Connecting);
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await socket.ConnectAsync(options.RendezvousUri, cancellationToken).ConfigureAwait(false);
                await RegisterAsync(socket, cancellationToken).ConfigureAwait(false);

                _socket = socket;
                backoff = TimeSpan.FromSeconds(1);
                SetState(RendezvousState.Online);

                // Two loops share the socket: the receive loop owns reads, the heartbeat loop owns the telemetry
                // channel. Sends are still serialised by _sendLock, so a heartbeat cannot interleave with a connect
                // request mid-frame.
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receive = ReceiveLoopAsync(socket, connection.Token);
                var heartbeat = TelemetryLoopAsync(socket, connection.Token);
                var sessionEnds = SessionEndLoopAsync(socket, connection.Token);
                try
                {
                    await receive.ConfigureAwait(false);
                }
                finally
                {
                    await connection.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAll(heartbeat, sessionEnds).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or InvalidDataException or SocketException or HttpRequestException)
            {
                LogConnectionLost(logger, ex.Message);
            }
            finally
            {
                _socket = null;
                FailPending();
                SetState(RendezvousState.Offline);
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    public async IAsyncEnumerable<IncomingConnection> ReadIncomingAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var incoming in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return incoming;
        }
    }

    /// <summary>Operator commands addressed to this device. Only present when it declared the capability.</summary>
    public IAsyncEnumerable<AdminCommand> ReadAdminCommandsAsync(CancellationToken cancellationToken) =>
        _admin.Reader.ReadAllAsync(cancellationToken);

    public async Task<ConnectReady> RequestConnectionAsync(DeviceId target, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ConnectReady>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        try
        {
            var request = new ConnectRequest { RequestId = requestId, TargetDeviceId = (uint)target.Value };
            request.Candidates.Add(candidates);
            await SendAsync(new ClientMessage { ConnectRequest = request }, cancellationToken).ConfigureAwait(false);

            // The server enforces its own timeout; ours only guards against a lost answer.
            return await completion.Task.WaitAsync(options.SignalingTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new PeerConnectException(PeerConnectFailure.Timeout, "The remote device did not answer.", ex);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public Task AcceptAsync(string requestId, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken)
    {
        var accept = new ConnectAccept { RequestId = requestId };
        accept.Candidates.Add(candidates);
        return SendAsync(new ClientMessage { ConnectAccept = accept }, cancellationToken);
    }

    public Task RejectAsync(string requestId, CancellationToken cancellationToken) =>
        SendAsync(new ClientMessage { ConnectReject = new ConnectReject { RequestId = requestId } }, cancellationToken);

    public void ReportTelemetry(TelemetryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Silently dropped when the queue is full or the device is offline. The alternative — awaiting a send from a
        // session's timer — would let the console's availability shape the session it is supposed to observe.
        _telemetry.Writer.TryWrite(report);
    }

    public void ReportSessionEnded(SessionEndedReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Same trade as a heartbeat: a report that cannot be queued is dropped rather than blocking the session's
        // shutdown. The other end of the session reports the same fact independently.
        _sessionEnds.Writer.TryWrite(report);
    }

    /// <summary>Drains the queue onto the socket while this connection is up.</summary>
    private async Task TelemetryLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var report in _telemetry.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await WebSocketMessaging.SendAsync(socket, new ClientMessage { Telemetry = report }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The receive loop reports the disconnect; a heartbeat that cannot be sent is not a second error.
        }
        finally
        {
            // Whatever was queued describes a connection that no longer exists.
            while (_telemetry.Reader.TryRead(out _))
            {
            }
        }
    }

    /// <summary>Drains end reports onto the socket while this connection is up.</summary>
    private async Task SessionEndLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var report in _sessionEnds.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await WebSocketMessaging.SendAsync(socket, new ClientMessage { SessionEnded = report }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The receive loop reports the disconnect; a report that cannot be sent is not a second error.
        }
        finally
        {
            // Whatever was queued describes a session on a connection that no longer exists.
            while (_sessionEnds.Reader.TryRead(out _))
            {
            }
        }
    }

    private async Task RegisterAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var challenge = await WebSocketMessaging.ReceiveAsync(socket, ServerMessage.Parser, cancellationToken).ConfigureAwait(false);
        if (challenge?.BodyCase != ServerMessage.BodyOneofCase.RegisterChallenge)
        {
            throw new InvalidDataException("Expected a registration challenge.");
        }

        var register = new Register
        {
            Certificate = ByteString.CopyFrom(identity.Certificate.RawData),
            Signature = ByteString.CopyFrom(identity.Sign(challenge.RegisterChallenge.Nonce.Span)),

            // The only capability this client has; the server gates admin_command on it.
            ClientCapabilities = ClientCapabilities.AdminCommands,
        };
        await WebSocketMessaging.SendAsync(socket, new ClientMessage { Register = register }, cancellationToken).ConfigureAwait(false);

        var registered = await WebSocketMessaging.ReceiveAsync(socket, ServerMessage.Parser, cancellationToken).ConfigureAwait(false);
        if (registered?.BodyCase != ServerMessage.BodyOneofCase.Registered)
        {
            throw new InvalidDataException("Registration was refused.");
        }

        // Both numbers were chosen by the server. Constructing DeviceId or an IPEndPoint from an out-of-range value
        // throws ArgumentOutOfRangeException, which is not what the transport's reconnect loop catches - one bad
        // message from a buggy or malicious server would stop the host service instead of being retried.
        if (!Domain.DeviceId.TryParse(
                ((uint)registered.Registered.DeviceId).ToString(CultureInfo.InvariantCulture),
                out var deviceId))
        {
            throw new InvalidDataException("The server sent an invalid device id.");
        }

        if (registered.Registered.ReflectorPort is < 1 or > 65535)
        {
            throw new InvalidDataException("The server sent an invalid reflector port.");
        }

        DeviceId = deviceId;
        ReflectorEndPoint = await ResolveReflectorAsync((int)registered.Registered.ReflectorPort, cancellationToken).ConfigureAwait(false);
        LogRegistered(logger, DeviceId.Value);
    }

    public async Task<IReadOnlySet<DeviceId>?> QueryPresenceAsync(IReadOnlyList<DeviceId> deviceIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);

        var online = new HashSet<DeviceId>();
        if (deviceIds.Count == 0)
        {
            return online;
        }

        if (_socket is null)
        {
            return null;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<PresenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPresence[requestId] = pending;
        try
        {
            var query = new PresenceQuery { RequestId = requestId };
            query.DeviceIds.AddRange(deviceIds.Select(static id => (uint)id.Value));
            await SendAsync(new ClientMessage { PresenceQuery = query }, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PresenceTimeout);
            var result = await pending.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

            foreach (var value in result.OnlineDeviceIds)
            {
                // Parsed rather than constructed: new DeviceId(int) throws on a value out of range, and this number was
                // chosen by the server. A malicious server must not be able to take the transport thread down with it.
                // Qualified: the property named DeviceId on this class hides the type of the same name here.
                if (Domain.DeviceId.TryParse(value.ToString(CultureInfo.InvariantCulture), out var id))
                {
                    online.Add(id);
                }
            }

            return online;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An older server ignores the message entirely and simply says nothing.
            LogPresenceUnanswered(logger);
            return null;
        }
        catch (PeerConnectException)
        {
            return null;
        }
        finally
        {
            _pendingPresence.TryRemove(requestId, out _);
        }
    }

    private async Task<IPEndPoint?> ResolveReflectorAsync(int port, CancellationToken cancellationToken)
    {
        var host = options.RendezvousUri.Host;
        if (IPAddress.TryParse(host, out var literal))
        {
            return new IPEndPoint(literal, port);
        }

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
        return addresses.Length == 0 ? null : new IPEndPoint(addresses[0], port);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await WebSocketMessaging.ReceiveAsync(socket, ServerMessage.Parser, cancellationToken).ConfigureAwait(false);
            switch (message?.BodyCase)
            {
                case null:
                    return;
                case ServerMessage.BodyOneofCase.IncomingConnection:
                    _incoming.Writer.TryWrite(message.IncomingConnection);
                    break;
                case ServerMessage.BodyOneofCase.ConnectReady:
                    if (_pending.TryGetValue(message.ConnectReady.RequestId, out var ready))
                    {
                        ready.TrySetResult(message.ConnectReady);
                    }

                    break;
                case ServerMessage.BodyOneofCase.ConnectFailed:
                    if (_pending.TryGetValue(message.ConnectFailed.RequestId, out var failed))
                    {
                        failed.TrySetException(ToException(message.ConnectFailed.Reason));
                    }

                    break;
                case ServerMessage.BodyOneofCase.PresenceResult:
                    if (_pendingPresence.TryGetValue(message.PresenceResult.RequestId, out var answered))
                    {
                        answered.TrySetResult(message.PresenceResult);
                    }

                    break;
                case ServerMessage.BodyOneofCase.AdminCommand:
                    _admin.Writer.TryWrite(message.AdminCommand);
                    break;
                default:
                    break;
            }
        }
    }

    private async Task SendAsync(IMessage message, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new PeerConnectException(PeerConnectFailure.SignalingUnavailable, "Not connected to the server.");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WebSocketMessaging.SendAsync(socket, message, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new PeerConnectException(PeerConnectFailure.SignalingUnavailable, "Not connected to the server.", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void FailPending()
    {
        var failure = new PeerConnectException(PeerConnectFailure.SignalingUnavailable, "Lost connection to the server.");
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(failure);
        }

        // A question that was in flight when the socket dropped is never going to be answered; leaving it pending would
        // hold the caller until its own timeout for no reason.
        foreach (var presence in _pendingPresence.Values)
        {
            presence.TrySetException(failure);
        }
    }

    private void SetState(RendezvousState state)
    {
        if (State != state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => _sendLock.Dispose();

    private static PeerConnectException ToException(FailureReason reason) => reason switch
    {
        FailureReason.TargetOffline => new(PeerConnectFailure.TargetOffline, "The remote device is offline."),
        FailureReason.Rejected => new(PeerConnectFailure.Rejected, "The remote device refused the connection."),
        FailureReason.Timeout => new(PeerConnectFailure.Timeout, "The remote device did not answer."),
        _ => new(PeerConnectFailure.NoPath, "The connection failed."),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered with the rendezvous server as {DeviceId}.")]
    private static partial void LogRegistered(ILogger logger, DeviceId deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rendezvous connection lost: {Reason}.")]
    private static partial void LogConnectionLost(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The server did not answer a presence query; device states are unknown.")]
    private static partial void LogPresenceUnanswered(ILogger logger);
}
