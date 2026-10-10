using System.Net;
using System.Net.Sockets;
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
    private readonly TimeProvider _time;
    private readonly ILogger<UdpRelay> _logger;
    private readonly Dictionary<Guid, Pairing> _pairings = [];
    private readonly Dictionary<IPEndPoint, Route> _routes = [];
    private DateTimeOffset _nextCleanup;

    public UdpRelay(IPEndPoint bindEndPoint, byte[] sharedKey, TimeSpan idleTimeout, TimeProvider time, ILogger<UdpRelay> logger)
    {
        ArgumentNullException.ThrowIfNull(bindEndPoint);

        _key = sharedKey;
        _idleTimeout = idleTimeout;
        _time = time;
        _logger = logger;
        _socket = new Socket(bindEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(bindEndPoint);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>Number of fully paired sessions (for metrics and tests).</summary>
    public int ActivePairings => _pairings.Values.Count(p => p.Controller is not null && p.Host is not null);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65_535];
        EndPoint any = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
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

            var from = (IPEndPoint)received.RemoteEndPoint;
            await HandleAsync(buffer.AsMemory(0, received.ReceivedBytes), from, cancellationToken).ConfigureAwait(false);
            CleanupIfDue();
        }
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
            LogRejectedBind(_logger, from);
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
            LogRejectedCookie(_logger, from);
            return BindResult.Ignored;
        }

        if (!_pairings.TryGetValue(ticket.SessionId, out var pairing))
        {
            if (_pairings.Count >= MaxSessions)
            {
                LogTooManySessions(_logger, _pairings.Count);
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

        pairing.LastActivity = _time.GetUtcNow();
        if (pairing.Controller is not null && pairing.Host is not null)
        {
            _routes[pairing.Controller] = new Route(pairing, pairing.Host);
            _routes[pairing.Host] = new Route(pairing, pairing.Controller);
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
        _pairings.Values.Any(p => p != except && (endPoint.Equals(p.Controller) || endPoint.Equals(p.Host)));

    private void Unroute(Pairing pairing)
    {
        if (pairing.Controller is not null)
        {
            _routes.Remove(pairing.Controller);
        }

        if (pairing.Host is not null)
        {
            _routes.Remove(pairing.Host);
        }
    }

    private void CleanupIfDue()
    {
        var now = _time.GetUtcNow();
        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now + TimeSpan.FromSeconds(10);
        foreach (var pairing in _pairings.Values.Where(p => now - p.LastActivity > _idleTimeout).ToList())
        {
            Unroute(pairing);
            _pairings.Remove(pairing.SessionId);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected relay bind from {EndPoint}: the token did not match this address.")]
    private static partial void LogRejectedCookie(ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a relay session: {Sessions} are already open.")]
    private static partial void LogTooManySessions(ILogger logger, int sessions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay session {SessionId}: {Role} moved from {Previous} to {Current}.")]
    private static partial void LogRebind(ILogger logger, Guid sessionId, RelayRole role, IPEndPoint previous, IPEndPoint current);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected relay bind from {EndPoint}: invalid or expired ticket.")]
    private static partial void LogRejectedBind(ILogger logger, IPEndPoint endPoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay session {SessionId} paired: {Controller} <-> {Host}.")]
    private static partial void LogPaired(ILogger logger, Guid sessionId, IPEndPoint controller, IPEndPoint host);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Relay send to {EndPoint} failed: {Error}.")]
    private static partial void LogSendFailed(ILogger logger, IPEndPoint endPoint, SocketError error);

    private sealed class Pairing(Guid sessionId)
    {
        public Guid SessionId { get; } = sessionId;

        public IPEndPoint? Controller { get; set; }

        public IPEndPoint? Host { get; set; }

        public DateTimeOffset LastActivity { get; set; }
    }

    private sealed record Route(Pairing Pairing, IPEndPoint Peer);
}
