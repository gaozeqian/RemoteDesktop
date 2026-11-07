using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

public static class ChannelEndpoints
{
    public sealed record UpsertChannelRequest(string? Id, string? Kind, string? Target, string? Secret, bool? Enabled);

    public static void MapChannelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var channels = endpoints.MapGroup("/api/settings/channels").RequireAuthorization(OpsAuth.ViewerPolicy);

        channels.MapGet("/", async (OpsStore store, CancellationToken cancellationToken) =>
        {
            var rows = await store.ListNotificationChannelsAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(rows.Select(c => new
            {
                id = c.Id,
                kind = c.Kind,
                target = c.Target,
                hasSecret = !string.IsNullOrEmpty(c.Secret),
                enabled = c.Enabled,
            }));
        });

        channels.MapPost("/", async (
            UpsertChannelRequest request,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var id = request.Id?.Trim() ?? Guid.NewGuid().ToString("N");
            var kind = request.Kind?.Trim().ToLowerInvariant();
            var target = request.Target?.Trim();

            if (string.IsNullOrEmpty(kind) || (kind != "webhook" && kind != "smtp"))
            {
                return Results.BadRequest(new { error = "kind", message = "kind must be 'webhook' or 'smtp'" });
            }

            if (string.IsNullOrEmpty(target))
            {
                return Results.BadRequest(new { error = "target", message = "target is required" });
            }

            var channel = new NotificationChannel(
                id,
                kind,
                target,
                request.Secret?.Trim(),
                request.Enabled ?? true);

            await store.UpsertNotificationChannelAsync(channel, cancellationToken).ConfigureAwait(false);
            await AuditAsync(
                store,
                context,
                context.User.Identity?.Name ?? "unknown",
                "notification.channel.save",
                channel.Id,
                time.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);

            return Results.Ok(new
            {
                id = channel.Id,
                kind = channel.Kind,
                target = channel.Target,
                hasSecret = !string.IsNullOrEmpty(channel.Secret),
                enabled = channel.Enabled,
            });
        }).RequireAuthorization(OpsAuth.AdminPolicy);

        channels.MapDelete("/{id}", async (
            string id,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (!await store.DeleteNotificationChannelAsync(id, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await AuditAsync(
                store,
                context,
                context.User.Identity?.Name ?? "unknown",
                "notification.channel.delete",
                id,
                time.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        }).RequireAuthorization(OpsAuth.AdminPolicy);
    }

    private static Task<string> AuditAsync(
        OpsStore store,
        HttpContext context,
        string username,
        string action,
        string target,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                now.ToUnixTimeMilliseconds(),
                username,
                action,
                target,
                "{}",
                context.Connection.RemoteIpAddress?.ToString() ?? "-",
                "success"),
            cancellationToken);
}
