using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Acts on operator commands the signaling server forwards to this device. The only command today is "disconnect this
/// session", which is exactly what the local user's disconnect button does — the difference is that it came from the
/// console, so the log line names the command id that the console's audit entry carries.
/// </summary>
/// <remarks>
/// A separate hosted service rather than another loop inside <see cref="HostSessionService"/>: that service's single
/// loop is the accept path, and mixing a second input into it would make both harder to reason about. When no
/// <see cref="IRendezvousClient"/> is composed (some tests), the service simply ends.
/// </remarks>
public sealed partial class AdminCommandService(
    IRendezvousClient? rendezvous,
    HostSessionService sessions,
    ILogger<AdminCommandService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (rendezvous is null)
        {
            return;
        }

        await foreach (var command in rendezvous.ReadAdminCommandsAsync(stoppingToken).ConfigureAwait(false))
        {
            Handle(command);
        }
    }

    private void Handle(AdminCommand command)
    {
        if (!string.Equals(command.Command, ClientCapabilities.Commands.DisconnectSession, StringComparison.Ordinal)
            || !SessionId.TryParse(command.SessionId, out var id))
        {
            LogIgnored(logger, command.CommandId, command.Command);
            return;
        }

        // Disconnect returns false when the session is already gone, which is still success for the operator's purpose.
        var found = sessions.Disconnect(id);
        LogHandled(logger, command.CommandId, id, found);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Operator command {CommandId}: disconnected session {SessionId} (was open: {WasOpen}).")]
    private static partial void LogHandled(ILogger logger, string commandId, SessionId sessionId, bool wasOpen);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored an unknown operator command {CommandId} ({Command}).")]
    private static partial void LogIgnored(ILogger logger, string commandId, string command);
}
