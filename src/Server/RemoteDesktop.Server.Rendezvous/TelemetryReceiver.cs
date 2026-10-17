using RemoteDesktop.Server.Shared;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Accepts client heartbeats from an authenticated signaling connection and turns each one into one console event.
/// </summary>
/// <remarks>
/// <para>
/// This is the only path where an end-user device writes into the console's database, so nothing here trusts the
/// message beyond its shape: <see cref="SessionTelemetry.TryCreate"/> closes the field sets, the per-device bucket caps
/// how much one device can write, and the reported device id comes from the connection rather than from the report.
/// </para>
/// <para>
/// The event's correlation id is the relay's session id when there is one, because the relay has already sent events
/// under that id and the console must enrich that record rather than create a second one. A direct (P2P) session has
/// no relay record, so its own session id is used.
/// </para>
/// </remarks>
public sealed partial class TelemetryReceiver(
    NodeEventPublisher events,
    ServerMetrics metrics,
    TimeProvider time,
    ILogger<TelemetryReceiver> logger)
{
    /// <summary>
    /// What one device may write. A host with the maximum sixteen sessions reports each twice a minute, so sixty a
    /// minute leaves room for controllers as well; the bucket exists to bound abuse, not to shape normal traffic.
    /// </summary>
    private readonly KeyedTokenBucket<uint> _budget = new(burst: 60, refill: TimeSpan.FromSeconds(1), time);
    /// <summary>Handles one report. Never throws and never answers: telemetry is fire-and-forget by contract.</summary>
    public void Handle(DeviceSession session, TelemetryReport report)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(report);

        if (!events.Enabled)
        {
            return;
        }

        // Spent before validation: a device sending malformed reports in a loop should cost itself, not the server.
        if (!_budget.TryTake(session.DeviceId))
        {
            metrics.TelemetryRefused();
            LogThrottled(logger, session.DeviceId);
            return;
        }

        if (!SessionTelemetry.TryCreate(
                report.SessionId,
                report.RelaySessionId,
                report.Role switch
                {
                    TelemetryRole.Controller => "controller",
                    TelemetryRole.Host => "host",
                    _ => null,
                },
                report.Mode,
                report.Transport switch
                {
                    TelemetryTransport.Direct => "direct",
                    TelemetryTransport.Relayed => "relayed",
                    _ => null,
                },
                (int)report.RttMillis,
                report.Platform,
                report.Version,
                out var telemetry,
                out var error))
        {
            metrics.TelemetryRefused();
            LogRefused(logger, session.DeviceId, error);
            return;
        }

        var correlation = Guid.Parse(telemetry.RelaySessionId ?? telemetry.SessionId);
        events.Publish(OpsEventKinds.SessionTelemetry, correlation, new
        {
            sessionId = telemetry.SessionId,
            relaySessionId = telemetry.RelaySessionId,
            role = telemetry.Role,
            mode = telemetry.Mode,
            transport = telemetry.Transport,
            rttMilliseconds = telemetry.RttMilliseconds,
            platform = telemetry.Platform,
            version = telemetry.Version,
            deviceId = session.DeviceId,
        });
        metrics.TelemetryReported();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored a telemetry report from device {DeviceId}: {Reason}.")]
    private static partial void LogRefused(ILogger logger, uint deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Throttled telemetry from device {DeviceId}.")]
    private static partial void LogThrottled(ILogger logger, uint deviceId);
}
