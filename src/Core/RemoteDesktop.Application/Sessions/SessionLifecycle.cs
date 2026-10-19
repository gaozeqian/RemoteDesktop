using RemoteDesktop.Application.Features;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Tells the signaling server that an established session has ended, from either end of it.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="SessionTelemetry"/> and not behind the telemetry switch. The relay reports a
/// close only for sessions it paired; a direct (P2P) session has no other end fact, so without this report the
/// console shows it as active forever. Both ends report independently, which also covers the case where one process
/// goes away before the other notices.
/// </remarks>
public sealed class SessionLifecycle(IRendezvousClient? rendezvous)
{
    /// <summary>Reports once when the controller's session raises <see cref="RemoteSession.Closed"/>.</summary>
    public void Track(RemoteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.Closed += (_, _) => Report(session.Context);
    }

    /// <summary>Reports once when the host's session end token is cancelled.</summary>
    public void Track(SessionContext context, CancellationToken end)
    {
        ArgumentNullException.ThrowIfNull(context);

        end.Register(() => Report(context));
    }

    private void Report(SessionContext context)
    {
        if (rendezvous is null)
        {
            return;
        }

        rendezvous.ReportSessionEnded(new SessionEndedReport
        {
            SessionId = context.Id.ToString(),

            // Empty on an older server, or on a deployment that configures no relay. The console then correlates by
            // SessionId, which is still the id both ends agreed on.
            RelaySessionId = context.Connection.RelaySessionId ?? string.Empty,
            Role = context.Role == SessionRole.Controller ? TelemetryRole.Controller : TelemetryRole.Host,
            Reason = "closed",
        });
    }
}
