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
                if (TryBind(body, from))
                {
                    await SendAsync(UdpControlPacket.Create(UdpControlPacketType.RelayBindAck, []), from, cancellationToken).ConfigureAwait(false);
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

    private bool TryBind(ReadOnlySpan<byte> body, IPEndPoint from)
    {
        if (body.Length < 1 || !Enum.IsDefined((RelayRole)body[0]))
        {
            return false;
        }

        var role = (RelayRole)body[0];
        var ticket = RelayTicket.Validate(body[1..], _key, _time.GetUtcNow());
        if (ticket is null)
        {
            LogRejectedBind(_logger, from);
            return false;
        }

        if (!_pairings.TryGetValue(ticket.SessionId, out var pairing))
        {
            pairing = new Pairing(ticket.SessionId);
            _pairings.Add(ticket.SessionId, pairing);
        }

        // A retransmitted bind from the same endpoint just gets another ack.
        var current = role == RelayRole.Controller ? pairing.Controller : pairing.Host;
        if (from.Equals(current))
        {
            return true;
        }

        // An endpoint can serve only one session; a rebind (new port) replaces the previous endpoint.
        if (_routes.ContainsKey(from) || IsBoundElsewhere(from, pairing))
        {
            return false;
        }

        if (current is not null)
        {
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

        return true;
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
