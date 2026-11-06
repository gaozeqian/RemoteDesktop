using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RemoteDesktop.Server.Shared;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Server.Relay;

/// <summary>
/// UDP forwarder. Each peer binds its endpoint with <see cref="UdpControlPacketType.RelayBind"/> and the ticket it got
/// from the rendezvous server; once both roles are bound, every packet from one is forwarded to the other untouched.
/// QUIC's TLS stays end to end: the relay never sees plaintext.
/// </summary>
/// <remarks>A single receive loop owns all state, so no locking is needed.</remarks>
public sealed partial class UdpRelay : IDisposable
{
    /// <summary>
    /// Ceiling on sessions held at once. A pairing is a few hundred bytes and lives until it goes idle, and every
    /// device that registers can mint tickets, so without a ceiling the table is something to fill on purpose.
    /// </summary>
    private const int MaxSessions = 10_000;

    private readonly Socket _socket;
    private readonly byte[] _key;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _cleanupInterval;
    private readonly TimeProvider _time;
    private readonly IOpsEventSink _events;
    private readonly string _nodeName;
    private readonly ILogger<UdpRelay> _logger;
    private readonly Dictionary<Guid, Pairing> _pairings = [];
    private readonly Dictionary<IPEndPoint, Route> _routes = [];

    // Reverse index: which pairing, if any, holds this endpoint. Kept beside _routes so "is this endpoint already
    // bound to another session?" is a lookup instead of a scan of every pairing.
    private readonly Dictionary<IPEndPoint, Pairing> _endpoints = [];
    private DateTimeOffset _nextCleanup;

    // The receive loop is unauthenticated and public on UDP. Logging every bad ticket makes a trivial packet flood a
    // disk and alert flood, so rejects are counted and a summary is written at most once a minute. Every way a bind
    // can be refused feeds the same summary: a token that does not match and a full table are as much a free packet
    // flood to anyone holding a valid ticket as a bad ticket is to anyone at all.
    private static readonly TimeSpan RejectedBindLogInterval = TimeSpan.FromMinutes(1);
    private DateTimeOffset _nextRejectedBindLog;
    private long _rejectedTickets;
    private long _rejectedCookies;
    private long _rejectedForCapacity;

    // Faults in packet handling are summarized the same way, and receive errors are reported once until a receive
    // succeeds again, so a persistent failure cannot flood the log while the loop waits it out.
    private static readonly TimeSpan ReceiveRetryDelay = TimeSpan.FromMilliseconds(100);
    private DateTimeOffset _nextHandlerFaultLog;
    private long _handlerFaults;
    private bool _receiveFaulted;

    // Exported numbers. The receive loop owns the tables above and runs on one thread; these five are read from an HTTP
    // thread, so they are touched only through Interlocked/Volatile and never while holding a reference to a pairing.
    private long _bytesToController;
    private long _bytesToHost;
    private long _packetsForwarded;
    private long _pairingsTotal;
    private int _activePairings;

    public UdpRelay(
        IPEndPoint bindEndPoint,
        byte[] sharedKey,
        TimeSpan idleTimeout,
        TimeProvider time,
        IOpsEventSink events,
        string nodeName,
        ILogger<UdpRelay> logger,
        TimeSpan? cleanupInterval = null)
    {
        ArgumentNullException.ThrowIfNull(bindEndPoint);

        _key = sharedKey;
        _idleTimeout = idleTimeout;
        _cleanupInterval = cleanupInterval ?? TimeSpan.FromSeconds(10);
        _time = time;
        _events = events;
        _nodeName = nodeName;
        _logger = logger;
        _socket = new Socket(bindEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(bindEndPoint);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>Number of fully paired sessions (for metrics and tests).</summary>
    public int ActivePairings => Volatile.Read(ref _activePairings);

    /// <summary>Counters and gauges for the metrics endpoint, read without touching the receive loop's tables.</summary>
    public RelaySnapshot Snapshot() => new(
        ActivePairings,
        Interlocked.Read(ref _bytesToController),
        Interlocked.Read(ref _bytesToHost),
        Interlocked.Read(ref _packetsForwarded),
        Interlocked.Read(ref _pairingsTotal));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65_535];
        EndPoint any = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        // One source for the whole loop, rescheduled after every packet and replaced only after it fires. Allocating
        // one per packet would put a cancellation registration on the forwarding path.
        CancellationTokenSource? receiveWindow = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                receiveWindow ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveWindow.CancelAfter(_cleanupInterval);

                SocketReceiveFromResult received;
                try
                {
                    received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, receiveWindow.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // No packet arrived for a whole cleanup interval. Without this wake-up the table was only swept
                    // after the next packet, so a pairing whose peers both went quiet stayed "active" forever and the
                    // console never received its relay_closed event.
                    receiveWindow.Dispose();
                    receiveWindow = null;
                    CleanupGuarded();
                    continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // ICMP port unreachable from a peer that went away; keep serving the others.
                    continue;
                }
                catch (SocketException ex)
                {
                    // Any other receive error (a network interface going away, no buffer space, a transient driver
                    // fault) used to leave this method, and the worker's default is to stop the whole process. The
                    // socket is usually usable again a moment later; the pause keeps a persistent error from spinning.
                    if (!_receiveFaulted)
                    {
                        _receiveFaulted = true;
                        LogReceiveFailed(_logger, ex.SocketErrorCode, ex);
                    }

                    try
                    {
                        await Task.Delay(ReceiveRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }
                catch (ObjectDisposedException)
                {
                    // The socket was closed under the loop: the relay is being disposed.
                    break;
                }

                if (_receiveFaulted)
                {
                    _receiveFaulted = false;
                    LogReceiveRecovered(_logger);
                }

                var from = (IPEndPoint)received.RemoteEndPoint;
                if (!await HandleGuardedAsync(buffer.AsMemory(0, received.ReceivedBytes), from, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                CleanupGuarded();
            }
        }
        finally
        {
            receiveWindow?.Dispose();
        }
    }

    /// <summary>
    /// Handles one packet and keeps the loop alive when that fails. A packet comes from the public internet and the
    /// handler reaches into the console's event queue, so neither a malformed packet nor a broken sink may take the
    /// relay down with every other session on it. Cancellation is not a failure and is left to propagate.
    /// </summary>
    /// <returns><c>false</c> when the relay is shutting down and the loop should end.</returns>
    private async ValueTask<bool> HandleGuardedAsync(ReadOnlyMemory<byte> packet, IPEndPoint from, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(packet, from, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordHandlerFault(ex);
            return true;
        }
    }

    private void CleanupGuarded()
    {
        try
        {
            CleanupIfDue();
        }
        catch (Exception ex)
        {
            RecordHandlerFault(ex);
        }
    }

    /// <summary>Logs the first fault at once, then at most one line a minute saying how many more there were.</summary>
    private void RecordHandlerFault(Exception exception)
    {
        _handlerFaults++;
        var now = _time.GetUtcNow();
        if (now < _nextHandlerFaultLog)
        {
            return;
        }

        LogHandlerFailed(_logger, _handlerFaults, exception);
        _nextHandlerFaultLog = now + RejectedBindLogInterval;
        _handlerFaults = 0;
    }

    private async ValueTask HandleAsync(ReadOnlyMemory<byte> packet, IPEndPoint from, CancellationToken cancellationToken)
    {
        if (UdpControlPacket.TryParse(packet.Span, out var type, out var body))
        {
            if (type == UdpControlPacketType.RelayBind)
            {
                var result = TryBind(body, from, out var cookie);
                if (result == BindResult.Bound)
                {
                    await SendAsync(UdpControlPacket.Create(UdpControlPacketType.RelayBindAck, []), from, cancellationToken).ConfigureAwait(false);
                }
                else if (result == BindResult.Challenge)
                {
                    await SendAsync(UdpControlPacket.Create(UdpControlPacketType.RelayBindChallenge, cookie), from, cancellationToken).ConfigureAwait(false);
                }
            }

            // Other control packets (e.g. punches meant for a peer) are never forwarded.
            return;
        }

        if (_routes.TryGetValue(from, out var route))
        {
            route.Pairing.LastActivity = _time.GetUtcNow();

            // Counted before the send: a failed send still cost the peer the datagram, and attributing bytes to the
            // direction that produced them is what makes the two counters add up to the traffic that was offered.
            var bytes = packet.Length;
            if (from.Equals(route.Pairing.Controller))
            {
                Interlocked.Add(ref _bytesToHost, bytes);
                route.Pairing.BytesToHost += bytes;
            }
            else
            {
                Interlocked.Add(ref _bytesToController, bytes);
                route.Pairing.BytesToController += bytes;
            }

            Interlocked.Increment(ref _packetsForwarded);
            await SendAsync(packet, route.Peer, cancellationToken).ConfigureAwait(false);
        }
    }

    private BindResult TryBind(ReadOnlySpan<byte> body, IPEndPoint from, out byte[] cookie)
    {
        cookie = [];
        if (!UdpControlPacket.TryParseRelayBind(body, out var roleByte, out var presented, out var ticketBytes)
            || !Enum.IsDefined((RelayRole)roleByte))
        {
            return BindResult.Ignored;
        }

        var role = (RelayRole)roleByte;
        var now = _time.GetUtcNow();
        var ticket = RelayTicket.Validate(ticketBytes, _key, now);
        if (ticket is null)
        {
            RecordRejectedBind(Rejection.Ticket, from, now);
            return BindResult.Ignored;
        }

        // A bind without a token gets one sent back to the address it came from. Only an address that really receives
        // there can finish the bind, which is what stops a ticket holder from aiming the relay at somebody else.
        if (IsEmpty(presented))
        {
            cookie = RelayCookie.Compute(_key, ticketBytes, roleByte, from, now);
            return BindResult.Challenge;
        }

        if (!RelayCookie.Verify(presented, _key, ticketBytes, roleByte, from, now))
        {
            RecordRejectedBind(Rejection.Cookie, from, now);
            return BindResult.Ignored;
        }

        if (!_pairings.TryGetValue(ticket.SessionId, out var pairing))
        {
            if (_pairings.Count >= MaxSessions)
            {
                RecordRejectedBind(Rejection.Capacity, from, now);
                return BindResult.Ignored;
            }

            pairing = new Pairing(ticket.SessionId);
            _pairings.Add(ticket.SessionId, pairing);
        }

        // A retransmitted bind from the same endpoint just gets another ack.
        var current = role == RelayRole.Controller ? pairing.Controller : pairing.Host;
        if (from.Equals(current))
        {
            return BindResult.Bound;
        }

        // An endpoint can serve only one session; a rebind (new port) replaces the previous endpoint. Worth a line:
        // with the ticket in hand this is the ordinary case of a client coming back on a new port, and without it,
        // it is somebody taking a session away from the peer that had it.
        if (_routes.ContainsKey(from) || IsBoundElsewhere(from, pairing))
        {
            return BindResult.Ignored;
        }

        if (current is not null)
        {
            LogRebind(_logger, pairing.SessionId, role, current, from);
            Unroute(pairing);
        }

        if (role == RelayRole.Controller)
        {
            pairing.Controller = from;
        }
        else
        {
            pairing.Host = from;
        }

        _endpoints[from] = pairing;

        pairing.LastActivity = _time.GetUtcNow();
        if (pairing.Controller is not null && pairing.Host is not null)
        {
            _routes[pairing.Controller] = new Route(pairing, pairing.Host);
            _routes[pairing.Host] = new Route(pairing, pairing.Controller);

            // Counted once per pairing, not once per bind: a rebind re-adds both routes and must not look like a new
            // session on the dashboard.
            if (!pairing.Routed)
            {
                pairing.Routed = true;
                Interlocked.Increment(ref _activePairings);
                Interlocked.Increment(ref _pairingsTotal);
                Publish(OpsEventKinds.RelayPaired, pairing.SessionId, new { relaySessionId = pairing.SessionId.ToString("D"), pairedAt = pairing.LastActivity });
            }

            LogPaired(_logger, pairing.SessionId, pairing.Controller, pairing.Host);
        }

        return BindResult.Bound;
    }

    private static bool IsEmpty(ReadOnlySpan<byte> cookie) => !cookie.ContainsAnyExcept((byte)0);

    private enum BindResult
    {
        /// <summary>Nothing to answer: the bind was not usable and no ack may be sent for it.</summary>
        Ignored,

        /// <summary>Bound; the sender gets an ack.</summary>
        Bound,

        /// <summary>Answer with the token in <c>cookie</c> and wait for the same bind to come back carrying it.</summary>
        Challenge,
    }

    private bool IsBoundElsewhere(IPEndPoint endPoint, Pairing except) =>
        _endpoints.TryGetValue(endPoint, out var owner) && owner != except;

    private void Unroute(Pairing pairing)
    {
        if (pairing.Controller is not null)
        {
            _routes.Remove(pairing.Controller);
            _endpoints.Remove(pairing.Controller);
        }

        if (pairing.Host is not null)
        {
            _routes.Remove(pairing.Host);
            _endpoints.Remove(pairing.Host);
        }
    }

    /// <summary>Queues one fact for the console. Skipped entirely when no console is configured.</summary>
    private void Publish(string kind, Guid correlationId, object payload)
    {
        if (!_events.Enabled)
        {
            return;
        }

        _events.Publish(new OpsEventEnvelope
        {
            EventId = Guid.CreateVersion7().ToString("D"),
            Node = _nodeName,
            Kind = kind,
            OccurredAt = _time.GetUtcNow(),
            CorrelationId = correlationId.ToString("D"),
            Payload = JsonSerializer.Serialize(payload, OpsEventJson.Options),
        });
    }

    private void CleanupIfDue()
    {
        var now = _time.GetUtcNow();
        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now + _cleanupInterval;
        foreach (var pairing in _pairings.Values.Where(p => now - p.LastActivity > _idleTimeout).ToList())
        {
            Unroute(pairing);
            _pairings.Remove(pairing.SessionId);
            if (pairing.Routed)
            {
                pairing.Routed = false;
                Interlocked.Decrement(ref _activePairings);
                Publish(OpsEventKinds.RelayClosed, pairing.SessionId, new
                {
                    relaySessionId = pairing.SessionId.ToString("D"),
                    bytesToController = pairing.BytesToController,
                    bytesToHost = pairing.BytesToHost,
                    closedAt = now,
                    reason = "idle",
                });
            }
        }
    }

    private async ValueTask SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint to, CancellationToken cancellationToken)
    {
        try
        {
            await _socket.SendToAsync(packet, SocketFlags.None, to, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            LogSendFailed(_logger, to, ex.SocketErrorCode);
        }
    }

    public void Dispose() => _socket.Dispose();

    private enum Rejection
    {
        /// <summary>The ticket is not signed by this deployment, or has expired.</summary>
        Ticket,

        /// <summary>The ticket is good but the token does not belong to this address and role.</summary>
        Cookie,

        /// <summary>Everything checked out and the relay already holds as many sessions as it allows.</summary>
        Capacity,
    }

    private void RecordRejectedBind(Rejection reason, IPEndPoint from, DateTimeOffset now)
    {
        switch (reason)
        {
            case Rejection.Ticket:
                _rejectedTickets++;
                break;
            case Rejection.Cookie:
                _rejectedCookies++;
                break;
            default:
                _rejectedForCapacity++;
                break;
        }

        if (now < _nextRejectedBindLog)
        {
            return;
        }

        LogRejectedBinds(_logger, from, _rejectedTickets, _rejectedCookies, _rejectedForCapacity, _pairings.Count);
        _nextRejectedBindLog = now + RejectedBindLogInterval;
        _rejectedTickets = 0;
        _rejectedCookies = 0;
        _rejectedForCapacity = 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected relay binds since the last summary: {Tickets} invalid or expired ticket, {Cookies} token that did not match the address, {Capacity} refused because {Sessions} sessions are open (most recently from {EndPoint}).")]
    private static partial void LogRejectedBinds(ILogger logger, IPEndPoint endPoint, long tickets, long cookies, long capacity, int sessions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relay receive failed with {Error}; retrying. Further receive errors are not logged until a receive succeeds.")]
    private static partial void LogReceiveFailed(ILogger logger, SocketError error, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay receive is working again.")]
    private static partial void LogReceiveRecovered(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relay packet handling failed ({Count} failures since the last report); the relay keeps serving.")]
    private static partial void LogHandlerFailed(ILogger logger, long count, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay session {SessionId}: {Role} moved from {Previous} to {Current}.")]
    private static partial void LogRebind(ILogger logger, Guid sessionId, RelayRole role, IPEndPoint previous, IPEndPoint current);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay session {SessionId} paired: {Controller} <-> {Host}.")]
    private static partial void LogPaired(ILogger logger, Guid sessionId, IPEndPoint controller, IPEndPoint host);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Relay send to {EndPoint} failed: {Error}.")]
    private static partial void LogSendFailed(ILogger logger, IPEndPoint endPoint, SocketError error);

    private sealed class Pairing(Guid sessionId)
    {
        public Guid SessionId { get; } = sessionId;

        public IPEndPoint? Controller { get; set; }

        public IPEndPoint? Host { get; set; }

        /// <summary>Whether this pairing is currently counted in <see cref="ActivePairings"/>.</summary>
        public bool Routed { get; set; }

        /// <summary>Bytes this pairing carried from hosts to controllers; reported when it closes.</summary>
        public long BytesToController { get; set; }

        /// <summary>Bytes this pairing carried from controllers to hosts.</summary>
        public long BytesToHost { get; set; }

        public DateTimeOffset LastActivity { get; set; }
    }

    private sealed record Route(Pairing Pairing, IPEndPoint Peer);
}
