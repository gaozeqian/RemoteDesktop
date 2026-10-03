using System.Collections.Concurrent;
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

    private readonly Channel<IncomingConnection> _incoming = Channel.CreateBounded<IncomingConnection>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ConnectReady>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
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
                await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
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
        };
        await WebSocketMessaging.SendAsync(socket, new ClientMessage { Register = register }, cancellationToken).ConfigureAwait(false);

        var registered = await WebSocketMessaging.ReceiveAsync(socket, ServerMessage.Parser, cancellationToken).ConfigureAwait(false);
        if (registered?.BodyCase != ServerMessage.BodyOneofCase.Registered)
        {
            throw new InvalidDataException("Registration was refused.");
        }

        DeviceId = new DeviceId((int)registered.Registered.DeviceId);
        ReflectorEndPoint = await ResolveReflectorAsync((int)registered.Registered.ReflectorPort, cancellationToken).ConfigureAwait(false);
        LogRegistered(logger, DeviceId.Value);
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
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new PeerConnectException(PeerConnectFailure.SignalingUnavailable, "Lost connection to the server."));
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
}
