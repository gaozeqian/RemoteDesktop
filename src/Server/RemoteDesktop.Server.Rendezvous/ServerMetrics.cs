using System.Globalization;
using System.Text;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// What this server counts about itself, written out by the <c>/metrics</c> endpoint in the Prometheus text exposition
/// format. Each event is one interlocked increment on the path that already carries it — counting never allocates,
/// never locks and never waits — and the endpoint itself is off unless an operator asks for it; see
/// <see cref="RendezvousOptions.MetricsEnabled"/>.
/// </summary>
public sealed class ServerMetrics
{
    /// <summary>The exposition format this endpoint speaks, as Prometheus is told to expect it.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private long _connectionsRefused;
    private long _registrations;
    private long _registrationsRefused;
    private long _connectRequestsAccepted;
    private long _connectRequestsThrottled;
    private long _connectRequestsRefused;
    private long _presenceQueriesThrottled;
    private long _relayTicketsIssued;
    private long _telemetryReported;
    private long _telemetryRefused;

    /// <summary>One connection was refused before the WebSocket upgrade because a quota was already full.</summary>
    public void ConnectionRefused() => Interlocked.Increment(ref _connectionsRefused);

    /// <summary>One device completed the certificate proof and was sent its id.</summary>
    public void Registered() => Interlocked.Increment(ref _registrations);

    /// <summary>One registration was refused because the caller's address had spent its budget.</summary>
    public void RegistrationRefused() => Interlocked.Increment(ref _registrationsRefused);

    /// <summary>One connect request passed the per-device budget and went into the pending table.</summary>
    public void ConnectRequestAccepted() => Interlocked.Increment(ref _connectRequestsAccepted);

    /// <summary>One connect request was refused because the requesting device had spent its budget.</summary>
    public void ConnectRequestThrottled() => Interlocked.Increment(ref _connectRequestsThrottled);

    /// <summary>One connect request was refused after the budgets passed, for want of room in the pending table.</summary>
    public void ConnectRequestRefused() => Interlocked.Increment(ref _connectRequestsRefused);

    /// <summary>One presence query was dropped because the asking device had spent its budget.</summary>
    public void PresenceQueryThrottled() => Interlocked.Increment(ref _presenceQueriesThrottled);

    /// <summary>One relay ticket was signed and handed to a controller. An accepted request mints one.</summary>
    public void RelayTicketIssued() => Interlocked.Increment(ref _relayTicketsIssued);

    /// <summary>One client heartbeat was validated and queued for the console.</summary>
    public void TelemetryReported() => Interlocked.Increment(ref _telemetryReported);

    /// <summary>One client heartbeat was dropped: malformed, out of range, or over the device's budget.</summary>
    public void TelemetryRefused() => Interlocked.Increment(ref _telemetryRefused);

    /// <summary>
    /// Writes every metric as its <c>HELP</c> line, its <c>TYPE</c> line and its sample. The two gauges are handed in
    /// rather than counted here: the connection quota and the presence table already know them, and a second count kept
    /// beside them could only drift out of step with the thing it claims to measure.
    /// </summary>
    public async Task WriteAsync(HttpContext context, int connectionsOpen, int devicesOnline)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.ContentType = ContentType;
        await context.Response.WriteAsync(Render(connectionsOpen, devicesOnline), context.RequestAborted).ConfigureAwait(false);
    }

    private string Render(int connectionsOpen, int devicesOnline)
    {
        (string Name, string Type, string Help, long Value)[] samples =
        [
            ("remotedesktop_connections_open", "gauge", "Connections being served right now.", connectionsOpen),
            ("remotedesktop_connections_refused_total", "counter", "Connections refused before the WebSocket upgrade because a quota was full.", Interlocked.Read(ref _connectionsRefused)),
            ("remotedesktop_registrations_total", "counter", "Devices that completed registration and were sent their id.", Interlocked.Read(ref _registrations)),
            ("remotedesktop_registrations_refused_total", "counter", "Registrations refused because the caller's address had spent its budget.", Interlocked.Read(ref _registrationsRefused)),
            ("remotedesktop_connect_requests_accepted_total", "counter", "Connect requests accepted into the pending table.", Interlocked.Read(ref _connectRequestsAccepted)),
            ("remotedesktop_connect_requests_throttled_total", "counter", "Connect requests refused because the requesting device had spent its budget.", Interlocked.Read(ref _connectRequestsThrottled)),
            ("remotedesktop_connect_requests_refused_total", "counter", "Connect requests refused after the budgets passed: no room in the pending table, or the request id was already waiting.", Interlocked.Read(ref _connectRequestsRefused)),
            ("remotedesktop_devices_online", "gauge", "Devices online right now.", devicesOnline),
            ("remotedesktop_presence_queries_throttled_total", "counter", "Presence queries dropped because the asking device had spent its budget.", Interlocked.Read(ref _presenceQueriesThrottled)),
            (OpsMetricNames.RelayTicketsIssued, "counter", "Relay tickets signed and handed to a controller.", Interlocked.Read(ref _relayTicketsIssued)),
            ("remotedesktop_telemetry_reports_total", "counter", "Client heartbeats validated and queued for the console.", Interlocked.Read(ref _telemetryReported)),
            ("remotedesktop_telemetry_refused_total", "counter", "Client heartbeats dropped: malformed, out of range, or over the device's budget.", Interlocked.Read(ref _telemetryRefused)),
        ];

        var text = new StringBuilder(1024);
        foreach (var (name, type, help, value) in samples)
        {
            // HELP has to come before TYPE for the same name, and the sample ends the metric; every line ends in a
            // newline because the format requires the last one to.
            text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
            text.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString();
    }
}
