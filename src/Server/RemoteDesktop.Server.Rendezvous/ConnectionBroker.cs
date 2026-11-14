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
    /// <summary>
    /// The most a request id may occupy. Clients mint a 32-character GUID; the bound exists because the id is a
    /// client-chosen string that is kept as a dictionary key and echoed back, and the pending table can hold
    /// thousands of them.
    /// </summary>
    private const int MaxRequestIdLength = 128;

    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);

    // The per-device count is kept beside the table rather than walked out of it, and both are updated under the same
    // gate so the ceiling and the table cannot drift apart under concurrency.
    private readonly Lock _pendingGate = new();
    private readonly ConcurrentDictionary<uint, int> _pendingPerDevice = new();
    private readonly DevicePresence _presence;
    private readonly KeyedTokenBucket<uint> _connectBudget;
    private readonly RendezvousOptions _options;
    private readonly ServerMetrics _metrics;
    private readonly NodeEventPublisher _events;
    private readonly SessionDirectory _sessions;
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
        SessionDirectory sessions,
        TimeProvider time,
        ILogger<ConnectionBroker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _presence = presence;
        _connectBudget = connectBudget;
        _options = options.Value;
        _metrics = metrics;
        _events = events;
        _sessions = sessions;
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
        if (host is null || host == controller)
        {
            await FailAsync(controller, request.RequestId, FailureReason.TargetOffline, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsValidRequestId(request.RequestId))
        {
            await FailAsync(controller, request.RequestId, FailureReason.Rejected, cancellationToken).ConfigureAwait(false);
            return;
        }

        var relay = CreateRelayInfo(out var correlationId);
        var pending = new PendingRequest(controller, host.DeviceId, relay, correlationId);
        if (!TryAdmit(request.RequestId, pending))
        {
            // Refused for one of three reasons: the global table is full, this device already has its share pending, or
            // the same request id is already waiting. All three get the same refusal.
            _metrics.ConnectRequestRefused();
            LogTooManyPendingRequests(_logger, controller.DeviceId, _pending.Count);
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
        _sessions.Register(pending.CorrelationId, pending.Controller.DeviceId, host.DeviceId);
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

    private static bool IsValidRequestId(string? requestId) =>
        requestId is { Length: <= MaxRequestIdLength } && Guid.TryParseExact(requestId, "N", out _);

    // Only the targeted host may answer a request.
    private bool TryComplete(DeviceSession host, string requestId, out PendingRequest pending)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (_pending.TryGetValue(requestId, out pending!) && pending.HostDeviceId == host.DeviceId)
        {
            return TryRemovePending(requestId, pending);
        }

        return false;
    }

    private async Task ExpireAsync(string requestId, PendingRequest pending)
    {
        await Task.Delay(_options.ConnectTimeout, _time).ConfigureAwait(false);
        if (TryRemovePending(requestId, pending))
        {
            Publish(OpsEventKinds.SessionFailed, pending.CorrelationId, new { reason = "timeout" });
            await FailAsync(pending.Controller, requestId, FailureReason.Timeout, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reserves a request under the gate: the ceiling check, the insert and the per-device count are one step, so two
    /// concurrent requests from the same device cannot both see space and both slip past the ceiling.
    /// </summary>
    private bool TryAdmit(string requestId, PendingRequest pending)
    {
        lock (_pendingGate)
        {
            if (_pending.Count >= _options.MaxPendingRequests
                || _pendingPerDevice.GetValueOrDefault(pending.Controller.DeviceId) >= _options.MaxPendingRequestsPerDevice)
            {
                return false;
            }

            if (!_pending.TryAdd(requestId, pending))
            {
                return false;
            }

            _pendingPerDevice.AddOrUpdate(pending.Controller.DeviceId, 1, static (_, count) => count + 1);
            return true;
        }
    }

    /// <summary>Removes a request and its per-device count together, so the counter cannot drift out of step.</summary>
    private bool TryRemovePending(string requestId, PendingRequest pending)
    {
        lock (_pendingGate)
        {
            if (!_pending.TryRemove(new KeyValuePair<string, PendingRequest>(requestId, pending)))
            {
                return false;
            }

            var remaining = _pendingPerDevice.AddOrUpdate(pending.Controller.DeviceId, 0, static (_, count) => count - 1);
            if (remaining <= 0)
            {
                // Device ids are cheap to mint; a zero left behind would let this map grow for the process lifetime.
                _pendingPerDevice.TryRemove(new KeyValuePair<uint, int>(pending.Controller.DeviceId, remaining));
            }

            return true;
        }
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
