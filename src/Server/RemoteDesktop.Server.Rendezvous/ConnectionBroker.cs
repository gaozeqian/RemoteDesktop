using System.Collections.Concurrent;
using System.Net;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Shared;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Pairs a controller's connect request with the host's answer: forwards candidates and certificate fingerprints both
/// ways and hands both sides the same relay ticket.
/// </summary>
public sealed partial class ConnectionBroker
{
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly DevicePresence _presence;
    private readonly RendezvousOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectionBroker> _logger;
    private readonly byte[]? _relayKey;
    private readonly PeerEndpoint? _relayEndpoint;

    public ConnectionBroker(DevicePresence presence, IOptions<RendezvousOptions> options, TimeProvider time, ILogger<ConnectionBroker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _presence = presence;
        _options = options.Value;
        _time = time;
        _logger = logger;
        if (!string.IsNullOrEmpty(_options.RelayEndpoint))
        {
            var relay = IPEndPoint.Parse(_options.RelayEndpoint);
            _relayEndpoint = new PeerEndpoint { Address = relay.Address.ToString(), Port = (uint)relay.Port };
            _relayKey = Convert.FromBase64String(_options.RelaySharedKey);
        }
    }

    public async Task HandleConnectRequestAsync(DeviceSession controller, ConnectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(request);

        var host = _presence.Find(request.TargetDeviceId);
        if (host is null || host == controller || string.IsNullOrEmpty(request.RequestId))
        {
            await FailAsync(controller, request.RequestId, FailureReason.TargetOffline, cancellationToken).ConfigureAwait(false);
            return;
        }

        var relay = CreateRelayInfo();
        var pending = new PendingRequest(controller, host.DeviceId, relay);
        if (!_pending.TryAdd(request.RequestId, pending))
        {
            await FailAsync(controller, request.RequestId, FailureReason.Rejected, cancellationToken).ConfigureAwait(false);
            return;
        }

        LogConnectRequested(_logger, request.RequestId, controller.DeviceId, host.DeviceId);
        _ = ExpireAsync(request.RequestId, pending);

        var incoming = new IncomingConnection
        {
            RequestId = request.RequestId,
            ControllerDeviceId = controller.DeviceId,
            ControllerFingerprint = controller.Fingerprint,
            Relay = relay,
        };
        incoming.ControllerCandidates.Add(request.Candidates);
        await host.TrySendAsync(new ServerMessage { IncomingConnection = incoming }, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleAcceptAsync(DeviceSession host, ConnectAccept accept, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accept);

        if (!TryComplete(host, accept.RequestId, out var pending))
        {
            return;
        }

        var ready = new ConnectReady { RequestId = accept.RequestId, HostFingerprint = host.Fingerprint, Relay = pending.Relay };
        ready.HostCandidates.Add(accept.Candidates);
        await pending.Controller.TrySendAsync(new ServerMessage { ConnectReady = ready }, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleRejectAsync(DeviceSession host, ConnectReject reject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reject);

        if (TryComplete(host, reject.RequestId, out var pending))
        {
            await FailAsync(pending.Controller, reject.RequestId, FailureReason.Rejected, cancellationToken).ConfigureAwait(false);
        }
    }

    // Only the targeted host may answer a request.
    private bool TryComplete(DeviceSession host, string requestId, out PendingRequest pending)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (_pending.TryGetValue(requestId, out pending!) && pending.HostDeviceId == host.DeviceId)
        {
            return _pending.TryRemove(new KeyValuePair<string, PendingRequest>(requestId, pending));
        }

        return false;
    }

    private async Task ExpireAsync(string requestId, PendingRequest pending)
    {
        await Task.Delay(_options.ConnectTimeout, _time).ConfigureAwait(false);
        if (_pending.TryRemove(new KeyValuePair<string, PendingRequest>(requestId, pending)))
        {
            await FailAsync(pending.Controller, requestId, FailureReason.Timeout, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private RelayInfo? CreateRelayInfo()
    {
        if (_relayKey is null)
        {
            return null;
        }

        var ticket = new RelayTicket(Guid.NewGuid(), _time.GetUtcNow() + _options.RelayTicketLifetime);
        return new RelayInfo { Endpoint = _relayEndpoint, Ticket = ByteString.CopyFrom(ticket.Sign(_relayKey)) };
    }

    private static Task FailAsync(DeviceSession controller, string requestId, FailureReason reason, CancellationToken cancellationToken) =>
        controller.TrySendAsync(
            new ServerMessage { ConnectFailed = new ConnectFailed { RequestId = requestId, Reason = reason } },
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connect request {RequestId}: {Controller} -> {Host}.")]
    private static partial void LogConnectRequested(ILogger logger, string requestId, uint controller, uint host);

    private sealed record PendingRequest(DeviceSession Controller, uint HostDeviceId, RelayInfo? Relay);
}
