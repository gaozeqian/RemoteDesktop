using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Reports one heartbeat per open session while <see cref="TelemetryOptions.Enabled"/> is on.
/// </summary>
/// <remarks>
/// <para>
/// The reporting code lives here rather than in the UI because both roles need it and neither should own it. The first
/// report goes out a few seconds after the session starts — early enough for a short session to be seen, late enough
/// for the first ping to have come back — and then once per interval.
/// </para>
/// <para>
/// Nothing here waits on the network: <see cref="IRendezvousClient.ReportTelemetry"/> only enqueues, so a console that
/// is down cannot slow a session down or make it fail.
/// </para>
/// </remarks>
public sealed class SessionTelemetry(TelemetryOptions options, IRendezvousClient? rendezvous, TimeProvider time)
{
    /// <summary>How long the first report waits, so that an RTT exists to put in it.</summary>
    private static readonly TimeSpan FirstReportDelay = TimeSpan.FromSeconds(5);

    /// <summary>Whether this process reports at all. Both the switch and a signaling connection are required.</summary>
    public bool Enabled => options.Enabled && rendezvous is not null;

    /// <summary>Follows a controller's session until it closes.</summary>
    public void Track(RemoteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!Enabled)
        {
            return;
        }

        var end = new CancellationTokenSource();
        session.Closed += (_, _) => end.Cancel();
        _ = ReportAsync(session.Context, end, () => session.RoundTripTime);
    }

    /// <summary>Follows a hosted session until its own end token is cancelled.</summary>
    public void Track(SessionContext context, CancellationToken end)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Enabled)
        {
            return;
        }

        // The host does not measure RTT: the controller's ping is answered, not timed, and a host-side number would be
        // a different measurement wearing the same name.
        _ = ReportAsync(context, CancellationTokenSource.CreateLinkedTokenSource(end), static () => null);
    }

    private async Task ReportAsync(SessionContext context, CancellationTokenSource end, Func<TimeSpan?> roundTrip)
    {
        // The token source is deliberately not disposed: the session's Closed handler may already have fired, and a
        // Cancel on a disposed source throws into the session's shutdown path. It owns no timer, so there is nothing
        // to release.
        try
        {
            await Task.Delay(FirstReportDelay < options.Interval ? FirstReportDelay : options.Interval, time, end.Token)
                .ConfigureAwait(false);

            while (!end.IsCancellationRequested)
            {
                rendezvous!.ReportTelemetry(Build(context, roundTrip()));

                await Task.Delay(options.Interval, time, end.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended; that is the normal way this loop stops.
        }
        catch (ObjectDisposedException)
        {
            // The session was disposed while a delay was in flight. Nothing left to report on.
        }
    }

    private static TelemetryReport Build(SessionContext context, TimeSpan? roundTrip)
    {
        var isController = context.Role == SessionRole.Controller;

        var report = new TelemetryReport
        {
            SessionId = context.Id.ToString(),
            Role = isController ? TelemetryRole.Controller : TelemetryRole.Host,
            Mode = context.Grant.Mode == SessionMode.View ? "view" : "control",
            Transport = context.Connection.Path == ConnectionPath.Relayed
                ? TelemetryTransport.Relayed
                : TelemetryTransport.Direct,
            // The commit suffix is deliberate here: two builds of the same release version must be distinguishable.
            Version = ProductVersion.Full,
        };

        if (context.Connection.RelaySessionId is { Length: > 0 } relaySessionId)
        {
            report.RelaySessionId = relaySessionId;
        }

        if (isController && roundTrip is { } rtt)
        {
            report.RttMillis = (uint)Math.Clamp((long)rtt.TotalMilliseconds, 0, 60_000);
        }

        // Only the host knows which platform is being controlled, and that is what the console's platform split is
        // about. A controller reporting its own would answer a different question under the same label.
        if (!isController)
        {
            report.Platform = Platform();
        }

        return report;
    }

    private static string Platform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return OperatingSystem.IsMacOS() ? "macos" : "linux";
    }
}
