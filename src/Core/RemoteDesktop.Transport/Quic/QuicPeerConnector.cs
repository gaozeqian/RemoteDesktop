using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Identity;
using RemoteDesktop.Transport.Nat;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Transport.Quic;

/// <summary>
/// Controller side. Learns its public endpoint, asks the server to broker the connection, punches towards the host's
/// candidates and tries them in order (LAN first); if none answers, goes through the relay.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed partial class QuicPeerConnector(
    IRendezvousClient rendezvous,
    DeviceIdentity identity,
    TransportOptions options,
    ILogger<QuicPeerConnector> logger) : IPeerConnector
{
    public async Task<IPeerConnection> ConnectAsync(DeviceId remote, CancellationToken cancellationToken)
    {
        if (!QuicConnection.IsSupported)
        {
            throw new PeerConnectException(PeerConnectFailure.NoPath, "QUIC is not supported on this system.");
        }

        var punch = PunchSocket.Bind();
        var attempts = new List<string>();
        try
        {
            var reflexive = rendezvous.ReflectorEndPoint is { } reflector
                ? await punch.ReflectAsync(reflector, options.UdpRequestTimeout, cancellationToken).ConfigureAwait(false)
                : null;
            var candidates = options.ForceRelay ? [] : CandidateGatherer.Gather(punch.Port, reflexive);

            var ready = await rendezvous.RequestConnectionAsync(remote, candidates.Select(CandidateGatherer.ToMessage), cancellationToken)
                .ConfigureAwait(false);
            var hostCandidates = CandidateGatherer.FromMessages(ready.HostCandidates);

            if (!options.ForceRelay && hostCandidates.Count > 0)
            {
                await punch.PunchAsync(hostCandidates, cancellationToken).ConfigureAwait(false);
                var port = punch.Port;

                // Hand the port (and the NAT mappings just opened) over to QUIC.
                punch.Dispose();
                foreach (var candidate in hostCandidates)
                {
                    var direct = await TryConnectAsync(candidate, port, ready.HostFingerprint, options.DirectAttemptTimeout, "direct", attempts, cancellationToken)
                        .ConfigureAwait(false);
                    if (direct is not null)
                    {
                        LogConnected(logger, remote, ConnectionPath.Direct, candidate);
                        return new QuicPeerConnection(direct, remote, ConnectionPath.Direct, identity.Fingerprint, ready.HostFingerprint);
                    }
                }
            }
            else
            {
                attempts.Add(options.ForceRelay
                    ? "direct attempts were skipped (the relay was forced)"
                    : "the device offered no direct candidate");
            }

            var relayed = await TryRelayAsync(ready, attempts, cancellationToken).ConfigureAwait(false);
            if (relayed is not null)
            {
                LogConnected(logger, remote, ConnectionPath.Relayed, relayed.RemoteEndPoint);
                return new QuicPeerConnection(relayed, remote, ConnectionPath.Relayed, identity.Fingerprint, ready.HostFingerprint);
            }

            // Everything that was tried, in the message. A connection that fails on somebody's machine is otherwise
            // unanswerable: this sentence is what the log has to work with, and "no path worked" on its own says
            // nothing about whether the candidates were wrong, the reflector was silent or the relay never answered.
            var seen = reflexive is null
                ? "the reflector did not report a public endpoint"
                : $"the reflector saw us at {reflexive}";
            throw new PeerConnectException(
                PeerConnectFailure.NoPath,
                $"No network path to the remote device worked ({seen}). Tried: {string.Join("; ", attempts)}.");
        }
        finally
        {
            punch.Dispose();
        }
    }

    private async Task<QuicConnection?> TryRelayAsync(ConnectReady ready, List<string> attempts, CancellationToken cancellationToken)
    {
        if (ready.Relay is null || !IPAddress.TryParse(ready.Relay.Endpoint?.Address, out var address))
        {
            attempts.Add("the server offered no relay");
            return null;
        }

        var relay = new IPEndPoint(address, (int)ready.Relay.Endpoint.Port);
        int port;
        using (var socket = PunchSocket.Bind())
        {
            if (!await socket.BindRelayAsync(relay, ready.Relay.Ticket.Span, RelayRole.Controller, options.UdpRequestTimeout, cancellationToken)
                    .ConfigureAwait(false))
            {
                LogRelayUnavailable(logger, relay);
                attempts.Add($"relay {relay} did not acknowledge the bind");
                return null;
            }

            port = socket.Port;
        }

        return await TryConnectAsync(relay, port, ready.HostFingerprint, options.RelayAttemptTimeout, "relay", attempts, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QuicConnection?> TryConnectAsync(IPEndPoint remote, int localPort, string expectedFingerprint, TimeSpan timeout, string kind, List<string> attempts, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(timeout);
        try
        {
            var connectionOptions = PeerTls.ClientOptions(identity, remote, new IPEndPoint(IPAddress.Any, localPort), expectedFingerprint);
            return await QuicConnection.ConnectAsync(connectionOptions, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogAttemptFailed(logger, remote, "timeout");
            attempts.Add($"{kind} {remote} did not answer within {timeout.TotalSeconds:0.#}s");
        }
        catch (Exception ex) when (ex is QuicException or AuthenticationException or System.Net.Sockets.SocketException)
        {
            LogAttemptFailed(logger, remote, ex.Message);
            attempts.Add($"{kind} {remote}: {ex.Message}");
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {Device} ({Path}) via {EndPoint}.")]
    private static partial void LogConnected(ILogger logger, DeviceId device, ConnectionPath path, IPEndPoint endPoint);

    // Information, not Debug: a candidate that did not answer is the first thing anybody debugging "it will not
    // connect" needs, and the number of these lines is the number of candidates — a handful.
    [LoggerMessage(Level = LogLevel.Information, Message = "Connection attempt to {EndPoint} failed: {Reason}.")]
    private static partial void LogAttemptFailed(ILogger logger, IPEndPoint endPoint, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relay {EndPoint} did not acknowledge the bind.")]
    private static partial void LogRelayUnavailable(ILogger logger, IPEndPoint endPoint);
}
