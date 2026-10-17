using System.Net;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Transport.Rendezvous;

public enum RendezvousState
{
    Offline,
    Connecting,
    Online,
}

/// <summary>Signaling connection to the rendezvous server: presence, device id and connect brokering.</summary>
public interface IRendezvousClient
{
    RendezvousState State { get; }

    /// <summary>Assigned by the server; <c>null</c> until the first registration succeeds.</summary>
    DeviceId? DeviceId { get; }

    /// <summary>UDP endpoint of the server's address reflector, known once online.</summary>
    IPEndPoint? ReflectorEndPoint { get; }

    event EventHandler? StateChanged;

    /// <summary>Keeps the connection alive, reconnecting with back-off, until cancelled.</summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Connection requests addressed to this device.</summary>
    IAsyncEnumerable<IncomingConnection> ReadIncomingAsync(CancellationToken cancellationToken);

    /// <summary>Operator commands addressed to this device, once a console exists and this device declared the capability.</summary>
    IAsyncEnumerable<AdminCommand> ReadAdminCommandsAsync(CancellationToken cancellationToken);

    /// <exception cref="PeerConnectException">Offline, target offline, rejected or timed out.</exception>
    Task<ConnectReady> RequestConnectionAsync(DeviceId target, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken);

    /// <summary>
    /// Which of <paramref name="deviceIds"/> the server reports as online.
    /// <para>
    /// <c>null</c> means the server did not answer — an older server, a throttled query, a lost connection — and must
    /// never be rendered as "offline": nothing is known about those devices. A device named in the answer was online
    /// when it was asked; one that is absent is either offline or was never registered, and the server deliberately
    /// does not distinguish the two.
    /// </para>
    /// </summary>
    Task<IReadOnlySet<DeviceId>?> QueryPresenceAsync(IReadOnlyList<DeviceId> deviceIds, CancellationToken cancellationToken);

    Task AcceptAsync(string requestId, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken);

    Task RejectAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues one heartbeat about a session this device is in. Never blocks and never retries: a report sent while
    /// offline, or replaced by a newer one before it went out, is simply not sent. The server does not answer.
    /// </summary>
    /// <remarks>
    /// Callers are expected to check their own telemetry switch first. This method exists so the switch lives in one
    /// place in the client's policy layer rather than in every call site.
    /// </remarks>
    void ReportTelemetry(TelemetryReport report);
}
