using RemoteDesktop.Server.Shared;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Accepts client heartbeats and session-end reports from an authenticated signaling connection and turns each one
/// into one console event.
/// </summary>
/// <remarks>
/// <para>
/// This is the only path where an end-user device writes into the console's database, so nothing here trusts the
/// message beyond its shape: <see cref="SessionTelemetry.TryCreate"/> closes the field sets, the per-device buckets cap
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

    /// <summary>
    /// What one device may report as ended. Ends are rare next to heartbeats; the bucket exists so a client cannot
    /// turn closing sessions into a write loop. Separate from the heartbeat bucket for the same reason the reports
    /// have their own queue: a burst of samples must not swallow the fact that closes a row.
    /// </summary>
    private readonly KeyedTokenBucket<uint> _endBudget = new(burst: 32, refill: TimeSpan.FromSeconds(1), time);

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

    /// <summary>
    /// Handles one session-end report. Both ends of a session may send one; the console folds the second into the same
    /// row, and a repeat is harmless. Never throws and never answers.
    /// </summary>
    public void HandleSessionEnded(DeviceSession session, SessionEndedReport report)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(report);

        if (!events.Enabled)
        {
            return;
        }

        if (!_endBudget.TryTake(session.DeviceId))
        {
            LogThrottledEnd(logger, session.DeviceId);
            return;
        }

        if (!TryReadEnd(report, out var sessionId, out var relaySessionId, out var role))
        {
            LogRefusedEnd(logger, session.DeviceId);
            return;
        }

        events.Publish(OpsEventKinds.SessionEnded, relaySessionId ?? sessionId, new
        {
            sessionId = sessionId.ToString("D"),
            relaySessionId = relaySessionId?.ToString("D"),
            role,
            reason = NormalizeReason(report.Reason),
        });
    }

    /// <summary>Parses the ids and role; the same shape checks a heartbeat gets, without the quality fields.</summary>
    private static bool TryReadEnd(SessionEndedReport report, out Guid sessionId, out Guid? relaySessionId, out string role)
    {
        sessionId = Guid.Empty;
        relaySessionId = null;
        role = string.Empty;

        if (!SessionTelemetry.IsGuid(report.SessionId)
            || (!string.IsNullOrEmpty(report.RelaySessionId) && !SessionTelemetry.IsGuid(report.RelaySessionId)))
        {
            return false;
        }

        sessionId = Guid.Parse(report.SessionId);
        if (!string.IsNullOrEmpty(report.RelaySessionId))
        {
            relaySessionId = Guid.Parse(report.RelaySessionId);
        }

        role = report.Role switch
        {
            TelemetryRole.Controller => "controller",
            TelemetryRole.Host => "host",
            _ => string.Empty,
        };
        return role.Length > 0;
    }

    /// <summary>A short token, not free text: the console displays it as the row's end reason.</summary>
    private static string NormalizeReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason)
        && reason.Length <= 32
        && reason.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? reason
            : "ended";

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored a telemetry report from device {DeviceId}: {Reason}.")]
    private static partial void LogRefused(ILogger logger, uint deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Throttled telemetry from device {DeviceId}.")]
    private static partial void LogThrottled(ILogger logger, uint deviceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored an end report from device {DeviceId}: the payload is not a session end.")]
    private static partial void LogRefusedEnd(ILogger logger, uint deviceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Throttled session-end reports from device {DeviceId}.")]
    private static partial void LogThrottledEnd(ILogger logger, uint deviceId);
}
