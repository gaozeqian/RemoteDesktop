using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Tells a client the public UDP endpoint its packets arrive from (a minimal STUN), which becomes its
/// server-reflexive candidate for hole punching.
/// </summary>
public sealed partial class AddressReflector : BackgroundService
{
    // A persistent receive error used to spin this loop at full speed with nothing in the log. This pause keeps a
    // broken socket from burning a core, and the flag keeps the first error from being buried under the rest.
    private static readonly TimeSpan ReceiveRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly Socket _socket;
    private readonly ILogger<AddressReflector> _logger;
    private bool _receiveFaulted;
    private bool _sendFaulted;

    public AddressReflector(IOptions<RendezvousOptions> options, ILogger<AddressReflector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
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
            catch (SocketException ex)
            {
                if (!_receiveFaulted)
                {
                    _receiveFaulted = true;
                    LogReceiveFailed(_logger, ex.SocketErrorCode, ex);
                }

                try
                {
                    await Task.Delay(ReceiveRetryDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (_receiveFaulted)
            {
                _receiveFaulted = false;
                LogReceiveRecovered(_logger);
            }

            if (UdpControlPacket.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var type, out var body) &&
                type == UdpControlPacketType.ReflectRequest && body.Length == UdpControlPacket.TransactionIdSize)
            {
                var from = (IPEndPoint)received.RemoteEndPoint;
                var response = UdpControlPacket.CreateReflectResponse(body, from);
                try
                {
                    await _socket.SendToAsync(response, SocketFlags.None, from, stoppingToken).ConfigureAwait(false);
                    _sendFaulted = false;
                }
                catch (SocketException ex)
                {
                    // One lost reflection answer only delays a punch; keep serving the other clients, but leave a
                    // trace the first time so a persistently unreachable path is not invisible.
                    if (!_sendFaulted)
                    {
                        _sendFaulted = true;
                        LogSendFailed(_logger, ex.SocketErrorCode, ex);
                    }
                }
            }
        }
    }

    public override void Dispose()
    {
        _socket.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDP reflector receive failed with {Error}; retrying. Further receive errors are not logged until a receive succeeds.")]
    private static partial void LogReceiveFailed(ILogger logger, SocketError error, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "UDP reflector receive is working again.")]
    private static partial void LogReceiveRecovered(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDP reflector could not send an answer ({Error}); further send errors are not logged until one succeeds.")]
    private static partial void LogSendFailed(ILogger logger, SocketError error, Exception exception);
}
