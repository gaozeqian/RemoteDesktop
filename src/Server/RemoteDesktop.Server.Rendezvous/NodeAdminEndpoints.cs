using System.Globalization;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Shared;
using RemoteDesktop.Server.Storage;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// The southbound half of the ops console's management actions: disconnect a session and revoke a device. Northbound
/// the console talks to these over the same Bearer-protected <c>/internal/v1</c> face it already uses for the device
/// list; southbound the request becomes either a signaling message to the device or a registry change.
/// </summary>
/// <remarks>
/// These are the only write endpoints on the internal API, so they are deliberately small and explicit about their
/// three failure modes: the device is offline, the device does not speak the command, or the id does not parse.
/// </remarks>
public static class NodeAdminEndpoints
{
    /// <summary>Maps <c>/internal/v1/admin/*</c>.</summary>
    public static void MapNodeAdmin(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/internal/v1/admin/disconnect", async (
            HttpContext context,
            DisconnectRequest request,
            DevicePresence presence,
            IOptions<RendezvousOptions> options) =>
        {
            if (!NodeTokenGuard.IsAuthorized(
                    context.Request.Headers.Authorization.ToString(),
                    options.Value.NodeToken,
                    context.Connection.RemoteIpAddress))
            {
                return Results.Unauthorized();
            }

            if (!uint.TryParse(request.DeviceId, NumberStyles.None, CultureInfo.InvariantCulture, out var deviceId)
                || !SessionIdText.TryParse(request.SessionId, out var sessionId))
            {
                return Results.BadRequest(new { error = "id", message = "deviceId or sessionId is not in the expected form" });
            }

            var device = presence.Find(deviceId);
            if (device is null)
            {
                return Results.NotFound(new { error = "offline", message = "the device holds no signaling connection" });
            }

            if (!device.Supports(ClientCapabilities.AdminCommands))
            {
                return Results.Json(
                    new { error = "unsupported", message = "the device's client does not declare the admin-command capability" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            await device.TrySendAsync(
                new ServerMessage
                {
                    AdminCommand = new AdminCommand
                    {
                        CommandId = Guid.CreateVersion7().ToString("D"),
                        Command = ClientCapabilities.Commands.DisconnectSession,
                        SessionId = sessionId,
                    },
                },
                context.RequestAborted).ConfigureAwait(false);

            return Results.Ok(new { delivered = true });
        });

        endpoints.MapPost("/internal/v1/admin/revoke", async (
            HttpContext context,
            RevokeRequest request,
            IDeviceRegistry registry,
            DevicePresence presence,
            IOptions<RendezvousOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!NodeTokenGuard.IsAuthorized(
                    context.Request.Headers.Authorization.ToString(),
                    options.Value.NodeToken,
                    context.Connection.RemoteIpAddress))
            {
                return Results.Unauthorized();
            }

            if (!uint.TryParse(request.DeviceId, NumberStyles.None, CultureInfo.InvariantCulture, out var deviceId))
            {
                return Results.BadRequest(new { error = "id", message = "deviceId is not in the expected form" });
            }

            await registry.SetRevokedAsync(deviceId, request.Revoked, cancellationToken).ConfigureAwait(false);

            // Revoking stops the next registration; kicking the current connection makes it take effect now rather
            // than whenever the device reconnects. The connection's own cleanup removes it from presence.
            if (request.Revoked)
            {
                presence.Find(deviceId)?.Abort();
            }

            return Results.Ok(new { revoked = request.Revoked });
        });
    }

    /// <summary>What the console posts to disconnect one session. Both ids are the host's, not the controller's.</summary>
    public sealed record DisconnectRequest(string? DeviceId, string? SessionId);

    /// <summary>What the console posts to revoke or un-revoke a device.</summary>
    public sealed record RevokeRequest(string? DeviceId, bool Revoked);
}

/// <summary>A session id in the form either end produces: the dashed form the console stores, or the 32-hex the client uses.</summary>
public static class SessionIdText
{
    /// <summary>Parses either UUID spelling into the 32-hex form <see cref="RemoteDesktop.Domain.SessionId"/> expects.</summary>
    public static bool TryParse(string? text, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!Guid.TryParseExact(text, "N", out var guid) && !Guid.TryParseExact(text, "D", out guid))
        {
            return false;
        }

        sessionId = guid.ToString("N");
        return true;
    }
}
