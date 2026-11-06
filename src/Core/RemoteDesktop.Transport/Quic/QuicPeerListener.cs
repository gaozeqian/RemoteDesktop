using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Identity;
using RemoteDesktop.Transport.Nat;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Transport.Quic;

/// <summary>
/// Host side. For each connect request: picks a fresh UDP port, learns its public endpoint, binds it to the relay,
/// punches towards the controller, then lets QUIC listen on that port and accepts only the controller's certificate.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed partial class QuicPeerListener(
    IRendezvousClient rendezvous,
    DeviceIdentity identity,
    TransportOptions options,
    ILogger<QuicPeerListener> logger) : IPeerListener
{
    /// <summary>How many connect requests may be preparing a port at once. Each one holds a UDP socket and a QUIC
    /// listener; the signaling server's request budget already limits how fast a device can ask, but not how many
    /// devices ask at the same time.</summary>
    private const int MaxConcurrentRequests = 32;

    private readonly Lock _requestGate = new();

    private int _requests;

    // Bounded, and dropping rather than blocking: if the session service cannot take an accepted connection, holding
    // an unbounded queue of live QUIC connections would be a slower way to run out of memory.
    private readonly Channel<IPeerConnection> _accepted = Channel.CreateBounded<IPeerConnection>(
        new BoundedChannelOptions(MaxConcurrentRequests)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!options.AcceptIncoming)
        {
            return;
        }

        if (!QuicListener.IsSupported)
        {
            LogQuicUnsupported(logger);
            return;
        }

        await foreach (var incoming in rendezvous.ReadIncomingAsync(cancellationToken).ConfigureAwait(false))
        {
            // Drop rather than queue without bound. The controller will time out and retry; nothing on this machine
            // is spent on a request that cannot be served now anyway.
            lock (_requestGate)
            {
                if (_requests >= MaxConcurrentRequests)
                {
                    LogTooManyRequests(logger, incoming.ControllerDeviceId, MaxConcurrentRequests);
                    continue;
                }

                _requests++;
            }

            _ = HandleAndReleaseAsync(incoming, cancellationToken);
        }
    }

    public ValueTask<IPeerConnection> AcceptAsync(CancellationToken cancellationToken) => _accepted.Reader.ReadAsync(cancellationToken);

    private async Task HandleAndReleaseAsync(IncomingConnection incoming, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(incoming, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_requestGate)
            {
                _requests--;
            }
        }
    }

    private async Task HandleAsync(IncomingConnection incoming, CancellationToken cancellationToken)
    {
        // The id is parsed inside the try: this runs as a task nobody awaits, and a request carrying a device id that
        // is not one — which is to say, a request from a signaling server that is not ours — must be dropped quietly
        // rather than becoming an exception nothing is left to observe.
        DeviceId? controller = null;
        try
        {
            var device = new DeviceId((int)incoming.ControllerDeviceId);
            controller = device;
            var (port, candidates, relay) = await PreparePortAsync(incoming, cancellationToken).ConfigureAwait(false);

            var listener = await QuicListener.ListenAsync(
                new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
                    ApplicationProtocols = [PeerTls.Alpn],
                    ConnectionOptionsCallback = (_, _, _) =>
                        ValueTask.FromResult(PeerTls.ServerOptions(identity, incoming.ControllerFingerprint)),
                },
                cancellationToken).ConfigureAwait(false);

            await using (listener.ConfigureAwait(false))
            {
                await rendezvous.AcceptAsync(incoming.RequestId, candidates.Select(CandidateGatherer.ToMessage), cancellationToken)
                    .ConfigureAwait(false);

                var connection = await AcceptControllerAsync(listener, cancellationToken).ConfigureAwait(false);
                if (connection is null)
                {
                    LogAcceptTimedOut(logger, device);
                    return;
                }

                // The listener is disposed when this block ends; the accepted connection lives on.
                var path = relay is not null && connection.RemoteEndPoint.Equals(relay) ? ConnectionPath.Relayed : ConnectionPath.Direct;
                LogAccepted(logger, device, path, connection.RemoteEndPoint);
                var peer = new QuicPeerConnection(
                    connection, device, path, identity.Fingerprint, incoming.ControllerFingerprint, SessionIdOf(incoming));
                if (!_accepted.Writer.TryWrite(peer))
                {
                    LogAcceptedQueueFull(logger, device);
                    await peer.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // A failed attempt must never take down the listener loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHandleFailed(logger, controller?.ToString() ?? incoming.ControllerDeviceId.ToString(CultureInfo.InvariantCulture), ex);
        }
    }

    private async Task<(int Port, List<IPEndPoint> Candidates, IPEndPoint? Relay)> PreparePortAsync(
        IncomingConnection incoming, CancellationToken cancellationToken)
    {
        using var punch = PunchSocket.Bind();

        var reflexive = rendezvous.ReflectorEndPoint is { } reflector
            ? await punch.ReflectAsync(reflector, options.UdpRequestTimeout, cancellationToken).ConfigureAwait(false)
            : null;

        // Bound up front so the relay fallback works even though the port is QUIC-owned afterwards.
        IPEndPoint? relay = null;
        // The endpoint came from the server. The connector validates the same value before using it; without this
        // check a zero or out-of-range port would throw out of the listener loop and hide behind a generic warning.
        if (incoming.Relay?.Endpoint is { } relayEndpoint
            && relayEndpoint.Port is >= 1 and <= 65535
            && IPAddress.TryParse(relayEndpoint.Address, out var relayAddress))
        {
            relay = new IPEndPoint(relayAddress, (int)relayEndpoint.Port);
            if (!await punch.BindRelayAsync(relay, incoming.Relay.Ticket.Span, RelayRole.Host, options.UdpRequestTimeout, cancellationToken)
                    .ConfigureAwait(false))
            {
                relay = null;
            }
        }

        await punch.PunchAsync(CandidateGatherer.FromMessages(incoming.ControllerCandidates), cancellationToken).ConfigureAwait(false);
        return (punch.Port, CandidateGatherer.Gather(punch.Port, reflexive), relay);
    }

    private async Task<QuicConnection?> AcceptControllerAsync(QuicListener listener, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.HostAcceptTimeout);
        while (true)
        {
            try
            {
                return await listener.AcceptConnectionAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex) when (ex is QuicException or AuthenticationException)
            {
                // Someone other than the expected controller (or a broken handshake); keep waiting for the real one.
                LogRejectedHandshake(logger, ex.Message);
            }
        }
    }

    /// <summary>
    /// The server-minted id both ends report under. The top-level field is always set by a current server; the relay's
    /// copy is the fallback for one that predates it.
    /// </summary>
    private static string? SessionIdOf(IncomingConnection incoming) =>
        string.IsNullOrEmpty(incoming.SessionId) ? incoming.Relay?.RelaySessionId : incoming.SessionId;

    [LoggerMessage(Level = LogLevel.Error, Message = "QUIC is not supported on this system; incoming connections are disabled.")]
    private static partial void LogQuicUnsupported(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted {Device} ({Path}) from {EndPoint}.")]
    private static partial void LogAccepted(ILogger logger, DeviceId device, ConnectionPath path, IPEndPoint endPoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Device} did not complete the connection in time.")]
    private static partial void LogAcceptTimedOut(ILogger logger, DeviceId device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped a connect request from {Device}: {Count} are already being prepared.")]
    private static partial void LogTooManyRequests(ILogger logger, uint device, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped an accepted connection from {Device}: the session queue is full.")]
    private static partial void LogAcceptedQueueFull(ILogger logger, DeviceId device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incoming connection from {Device} failed.")]
    private static partial void LogHandleFailed(ILogger logger, string device, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rejected QUIC handshake: {Reason}.")]
    private static partial void LogRejectedHandshake(ILogger logger, string reason);
}
