using System.Globalization;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Hosting;
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
    private static readonly TimeSpan AdminCommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Maps <c>/internal/v1/admin/*</c>.</summary>
    public static void MapNodeAdmin(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/internal/v1/admin/disconnect", async (
            HttpContext context,
            DisconnectRequest request,
            DevicePresence presence) =>
        {
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

            var command = new AdminCommand
            {
                CommandId = Guid.CreateVersion7().ToString("D"),
                Command = ClientCapabilities.Commands.DisconnectSession,
                SessionId = sessionId,
            };
            try
            {
                await device.ExecuteAdminCommandAsync(command, AdminCommandTimeout, context.RequestAborted).ConfigureAwait(false);
                return Results.Ok(new { delivered = true, executed = true });
            }
            catch (TimeoutException)
            {
                return Results.Json(new { error = "timeout", message = "the device did not acknowledge the command in time" }, statusCode: 504);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "refused", message = ex.Message }, statusCode: 409);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException)
            {
                return Results.Json(new { error = "delivery", message = ex.Message }, statusCode: 503);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { error = "delivery", message = "the device connection closed before acknowledgment" }, statusCode: 503);
            }
        }).RequireNodeToken(context =>
            context.RequestServices.GetRequiredService<IOptions<RendezvousOptions>>().Value.NodeToken);

        endpoints.MapPost("/internal/v1/admin/revoke", async (
            RevokeRequest request,
            IDeviceRegistry registry,
            DevicePresence presence,
            CancellationToken cancellationToken) =>
        {
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
        }).RequireNodeToken(context =>
            context.RequestServices.GetRequiredService<IOptions<RendezvousOptions>>().Value.NodeToken);
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
