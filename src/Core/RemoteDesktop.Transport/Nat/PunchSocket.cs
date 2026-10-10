using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Transport.Nat;

/// <summary>
/// Plain UDP socket used before QUIC takes over a port: learns the public endpoint, opens NAT mappings towards the
/// peer and binds the port to the relay. It is then disposed and QUIC binds the same port, inheriting the mappings.
/// </summary>
/// <remarks>Operations must not run concurrently; each owns the receive side while it runs.</remarks>
public sealed class PunchSocket : IDisposable
{
    private static readonly TimeSpan RetransmitInterval = TimeSpan.FromMilliseconds(250);

    private readonly Socket _socket;

    private PunchSocket(Socket socket) => _socket = socket;

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public static PunchSocket Bind(int port = 0)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // SIO_UDP_CONNRESET off: an ICMP "port unreachable" must not fail the next receive.
                socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            }

            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return new PunchSocket(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Asks the reflector which public endpoint it sees. Returns <c>null</c> on timeout.</summary>
    public async Task<IPEndPoint?> ReflectAsync(IPEndPoint reflector, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var transactionId = RandomNumberGenerator.GetBytes(UdpControlPacket.TransactionIdSize);
        var request = UdpControlPacket.Create(UdpControlPacketType.ReflectRequest, transactionId);
        IPEndPoint? observed = null;

        var answered = await RequestAsync(request, reflector, timeout, (type, body) =>
        {
            if (type == UdpControlPacketType.ReflectResponse &&
                UdpControlPacket.TryParseReflectResponse(body, out var id, out var endPoint) &&
                id.SequenceEqual(transactionId))
            {
                observed = endPoint;
                return [];
            }

            return null;
        }, cancellationToken).ConfigureAwait(false);

        return answered is null ? null : observed;
    }

    /// <summary>
    /// Binds this port to a relay session. Returns <c>false</c> when the relay did not acknowledge.
    /// </summary>
    /// <remarks>
    /// Two steps: the relay answers a bind that carries no token with one, sent to the address the bind came from,
    /// and the bind only takes effect when it comes back carrying it. That is what keeps the relay from being aimed
    /// at an address that never asked for it — and it costs one round trip on the relay path only.
    /// </remarks>
    public Task<bool> BindRelayAsync(IPEndPoint relay, ReadOnlySpan<byte> ticket, RelayRole role, TimeSpan timeout, CancellationToken cancellationToken) =>
        // The ticket is copied here because it has to outlive an await, which a span cannot do.
        BindRelayCoreAsync(relay, ticket.ToArray(), role, timeout, cancellationToken);

    private async Task<bool> BindRelayCoreAsync(IPEndPoint relay, byte[] ticket, RelayRole role, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var roleByte = (byte)role;
        var challenge = await RequestAsync(
            UdpControlPacket.CreateRelayBind(roleByte, [], ticket),
            relay,
            timeout,
            (type, body) => type == UdpControlPacketType.RelayBindChallenge ? body.ToArray() : null,
            cancellationToken).ConfigureAwait(false);

        if (challenge is null || challenge.Length != UdpControlPacket.RelayBindCookieSize)
        {
            return false;
        }

        return await RequestAsync(
            UdpControlPacket.CreateRelayBind(roleByte, challenge, ticket),
            relay,
            timeout,
            (type, _) => type == UdpControlPacketType.RelayBindAck ? [] : null,
            cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Sends a few punch packets to each target so our NAT accepts the peer's packets on this port.</summary>
    public async Task PunchAsync(IEnumerable<IPEndPoint> targets, CancellationToken cancellationToken)
    {
        var packet = UdpControlPacket.Create(UdpControlPacketType.Punch, []);
        var list = targets.ToList();
        for (var round = 0; round < 3; round++)
        {
            foreach (var target in list)
            {
                await TrySendAsync(packet, target, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _socket.Dispose();

    /// <summary>
    /// Decides whether a packet is the answer being waited for, and what to carry back from it:
    /// <c>null</c> when it is not the answer, an empty array when it is but there is nothing to carry.
    /// </summary>
    private delegate byte[]? ResponseFilter(UdpControlPacketType type, ReadOnlySpan<byte> body);

    /// <returns>The body of the matching response, or <c>null</c> when the deadline passed without one.</returns>
    private async Task<byte[]?> RequestAsync(byte[] request, IPEndPoint target, TimeSpan timeout, ResponseFilter filter, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var buffer = new byte[1500];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (true)
            {
                await TrySendAsync(request, target, deadline.Token).ConfigureAwait(false);

                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(RetransmitInterval);
                try
                {
                    while (true)
                    {
                        var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, attempt.Token).ConfigureAwait(false);
                        // Everything that touches the span stays inside this one expression: a span cannot be held
                        // across the await that starts the next round of the loop.
                        if (received.RemoteEndPoint.Equals(target) &&
                            UdpControlPacket.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var type, out var body) &&
                            filter(type, body) is { } answer)
                        {
                            return answer;
                        }
                    }
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    // Retransmit.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task TrySendAsync(byte[] packet, IPEndPoint target, CancellationToken cancellationToken)
    {
        try
        {
            await _socket.SendToAsync(packet, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Unreachable candidate (e.g. an interface without a route); other targets may still work.
        }
    }
}
