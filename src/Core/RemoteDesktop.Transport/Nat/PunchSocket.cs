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
                return true;
            }

            return false;
        }, cancellationToken).ConfigureAwait(false);

        return answered ? observed : null;
    }

    /// <summary>Binds this port to a relay session. Returns <c>false</c> when the relay did not acknowledge.</summary>
    public Task<bool> BindRelayAsync(IPEndPoint relay, ReadOnlySpan<byte> ticket, RelayRole role, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var body = new byte[1 + ticket.Length];
        body[0] = (byte)role;
        ticket.CopyTo(body.AsSpan(1));
        var request = UdpControlPacket.Create(UdpControlPacketType.RelayBind, body);

        return RequestAsync(request, relay, timeout, (type, _) => type == UdpControlPacketType.RelayBindAck, cancellationToken);
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

    private delegate bool ResponseFilter(UdpControlPacketType type, ReadOnlySpan<byte> body);

    private async Task<bool> RequestAsync(byte[] request, IPEndPoint target, TimeSpan timeout, ResponseFilter filter, CancellationToken cancellationToken)
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
                        if (received.RemoteEndPoint.Equals(target) &&
                            UdpControlPacket.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var type, out var body) &&
                            filter(type, body))
                        {
                            return true;
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
            return false;
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
