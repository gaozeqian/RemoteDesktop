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
    private readonly KeyedTokenBucket<uint> _connectBudget;
    private readonly RendezvousOptions _options;
    private readonly ServerMetrics _metrics;
    private readonly NodeEventPublisher _events;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectionBroker> _logger;
    private readonly byte[]? _relayKey;
    private readonly PeerEndpoint? _relayEndpoint;

    public ConnectionBroker(
        DevicePresence presence,
        KeyedTokenBucket<uint> connectBudget,
        IOptions<RendezvousOptions> options,
        ServerMetrics metrics,
        NodeEventPublisher events,
        TimeProvider time,
        ILogger<ConnectionBroker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _presence = presence;
        _connectBudget = connectBudget;
        _options = options.Value;
        _metrics = metrics;
        _events = events;
        _time = time;
        _logger = logger;
        if (!string.IsNullOrEmpty(_options.RelayEndpoint))
        {
            var relay = IPEndPoint.Parse(_options.RelayEndpoint);

            // The key signs the tickets that let a peer use the relay. RendezvousOptions.Validate has already refused an
            // empty, malformed or too-short one at start-up (AddRendezvous wires it up with ValidateOnStart), so what is
            // parsed here always satisfies the relay's own check.
            _relayEndpoint = new PeerEndpoint { Address = relay.Address.ToString(), Port = (uint)relay.Port };
            _relayKey = Convert.FromBase64String(_options.RelaySharedKey);
        }
    }

    public async Task HandleConnectRequestAsync(DeviceSession controller, ConnectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(request);

        // Counted before anything else: a device asking in a loop is what a stream of connection popups on somebody
        // else's machine looks like from here, and every accepted request also mints a relay ticket.
        if (!_connectBudget.TryTake(controller.DeviceId))
        {
            _metrics.ConnectRequestThrottled();
            LogThrottledConnectRequest(_logger, controller.DeviceId);
            await FailAsync(controller, request.RequestId, FailureReason.Rejected, cancellationToken).ConfigureAwait(false);
            return;
        }

        var host = _presence.Find(request.TargetDeviceId);
        if (host is null || host == controller || string.IsNullOrEmpty(request.RequestId))
        {
            await FailAsync(controller, request.RequestId, FailureReason.TargetOffline, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_pending.Count >= _options.MaxPendingRequests || PendingFor(controller.DeviceId) >= _options.MaxPendingRequestsPerDevice)
        {
            _metrics.ConnectRequestRefused();
            LogTooManyPendingRequests(_logger, controller.DeviceId, _pending.Count);
            await FailAsync(controller, request.RequestId, FailureReason.Rejected, cancellationToken).ConfigureAwait(false);
            return;
        }

        var relay = CreateRelayInfo(out var correlationId);
        var pending = new PendingRequest(controller, host.DeviceId, relay, correlationId);
        if (!_pending.TryAdd(request.RequestId, pending))
        {
            // The same request id is already waiting: a refusal this request id gets instead of a second entry.
            _metrics.ConnectRequestRefused();
            await FailAsync(controller, request.RequestId, FailureReason.Rejected, cancellationToken).ConfigureAwait(false);
            return;
        }

        _metrics.ConnectRequestAccepted();
        LogConnectRequested(_logger, request.RequestId, controller.DeviceId, host.DeviceId);
        Publish(
            OpsEventKinds.SessionRequested,
            correlationId,
            new { controllerDeviceId = controller.DeviceId, hostDeviceId = host.DeviceId, relaySessionId = relay is null ? null : correlationId.ToString("D") });
        _ = ExpireAsync(request.RequestId, pending);

        var incoming = new IncomingConnection
        {
            RequestId = request.RequestId,
            ControllerDeviceId = controller.DeviceId,
            ControllerFingerprint = controller.Fingerprint,
            Relay = relay,

            // Independent of the relay: even a deployment that configures none needs the id both ends report under.
            SessionId = correlationId.ToString("D"),
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

        var ready = new ConnectReady
        {
            RequestId = accept.RequestId,
            HostFingerprint = host.Fingerprint,
            Relay = pending.Relay,
            SessionId = pending.CorrelationId.ToString("D"),
        };
        ready.HostCandidates.Add(accept.Candidates);
        await pending.Controller.TrySendAsync(new ServerMessage { ConnectReady = ready }, cancellationToken).ConfigureAwait(false);
        Publish(OpsEventKinds.SessionAccepted, pending.CorrelationId, new { hostDeviceId = host.DeviceId });
    }

    public async Task HandleRejectAsync(DeviceSession host, ConnectReject reject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reject);

        if (TryComplete(host, reject.RequestId, out var pending))
        {
            Publish(OpsEventKinds.SessionFailed, pending.CorrelationId, new { reason = "rejected" });
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
            Publish(OpsEventKinds.SessionFailed, pending.CorrelationId, new { reason = "timeout" });
            await FailAsync(pending.Controller, requestId, FailureReason.Timeout, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Counts by walking the table instead of keeping a counter beside it: the table is capped, the requests that get
    /// this far are rate limited, and a number that cannot drift out of step with the table is worth more than the
    /// microseconds the walk costs.
    /// </summary>
    private int PendingFor(uint deviceId)
    {
        var count = 0;
        foreach (var (_, pending) in _pending)
        {
            if (pending.Controller.DeviceId == deviceId)
            {
                count++;
            }
        }

        return count;
    }

    private RelayInfo? CreateRelayInfo(out Guid correlationId)
    {
        // Minted even when there is no relay: the console correlates the request and its outcome by this id, and a
        // client-supplied identifier would let one connection address another's timeline.
        correlationId = Guid.NewGuid();
        if (_relayKey is null)
        {
            return null;
        }

        var ticket = new RelayTicket(correlationId, _time.GetUtcNow() + _options.RelayTicketLifetime);
        _metrics.RelayTicketIssued();
        return new RelayInfo
        {
            Endpoint = _relayEndpoint,
            Ticket = ByteString.CopyFrom(ticket.Sign(_relayKey)),

            // The same id, in the form both ends write: the client echoes it in telemetry so the console attaches
            // quality to the relay's record of this session instead of creating a second one for it.
            RelaySessionId = correlationId.ToString("D"),
        };
    }

    /// <summary>Queues one fact for the console. Building the envelope is skipped when no console is configured.</summary>
    private void Publish(string kind, Guid correlationId, object payload) => _events.Publish(kind, correlationId, payload);

    private static Task FailAsync(DeviceSession controller, string requestId, FailureReason reason, CancellationToken cancellationToken) =>
        controller.TrySendAsync(
            new ServerMessage { ConnectFailed = new ConnectFailed { RequestId = requestId, Reason = reason } },
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connect request {RequestId}: {Controller} -> {Host}.")]
    private static partial void LogConnectRequested(ILogger logger, string requestId, uint controller, uint host);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Throttled connect requests from device {DeviceId}.")]
    private static partial void LogThrottledConnectRequest(ILogger logger, uint deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a connect request from device {DeviceId}: {Pending} requests are already waiting.")]
    private static partial void LogTooManyPendingRequests(ILogger logger, uint deviceId, int pending);

    private sealed record PendingRequest(DeviceSession Controller, uint HostDeviceId, RelayInfo? Relay, Guid CorrelationId);
}
