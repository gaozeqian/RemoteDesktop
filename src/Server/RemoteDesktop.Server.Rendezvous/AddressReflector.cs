using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Tells a client the public UDP endpoint its packets arrive from (a minimal STUN), which becomes its
/// server-reflexive candidate for hole punching.
/// </summary>
public sealed class AddressReflector : BackgroundService
{
    private readonly Socket _socket;

    public AddressReflector(IOptions<RendezvousOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, options.Value.ReflectorPort));
    }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new byte[512];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!stoppingToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            if (UdpControlPacket.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var type, out var body) &&
                type == UdpControlPacketType.ReflectRequest && body.Length == UdpControlPacket.TransactionIdSize)
            {
                var from = (IPEndPoint)received.RemoteEndPoint;
                var response = UdpControlPacket.CreateReflectResponse(body, from);
                try
                {
                    await _socket.SendToAsync(response, SocketFlags.None, from, stoppingToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                }
            }
        }
    }

    public override void Dispose()
    {
        _socket.Dispose();
        base.Dispose();
    }
}
