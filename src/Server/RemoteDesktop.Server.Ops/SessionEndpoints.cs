using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// The session list and one session's timeline. Both are projections of stored events, so the detail view can say
/// exactly which facts produced the row.
/// </summary>
public static class SessionEndpoints
{
    /// <summary>Maps <c>/api/sessions</c>.</summary>
    public static void MapOpsSessions(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/sessions").RequireAuthorization(OpsAuth.ViewerPolicy);

        group.MapGet("/", async (int? limit, long? before, string? beforeId, OpsStore store, CancellationToken cancellationToken) =>
        {
            var rows = await store
                .ListSessionsAsync(limit ?? 50, before, beforeId, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                items = rows.Select(Describe),

                // Both halves of the cursor leave together. A client that sends only the timestamp still gets the
                // old behaviour, but it can repeat or skip rows that share a millisecond.
                nextBefore = rows.Count == 0 ? (long?)null : rows[^1].StartedAt ?? rows[^1].UpdatedAt,
                nextBeforeId = rows.Count == 0 ? null : rows[^1].Id,
            });
        });

        group.MapGet("/{id}", async (string id, OpsStore store, CancellationToken cancellationToken) =>
        {
            var session = await store.FindSessionAsync(id, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                return Results.NotFound();
            }

            var events = await store.ReadSessionEventsAsync(session.CorrelationId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                session = Describe(session),
                events = events.Select(row => new
                {
                    eventId = row.EventId,
                    kind = row.Kind,
                    node = row.Node,
                    occurredAt = row.OccurredAt,
                    receivedAt = row.ReceivedAt,
                    payload = row.Payload,
                }),
            });
        });
    }

    private static object Describe(OpsSessionRow row) => new
    {
        id = row.Id,
        correlationId = row.CorrelationId,
        controllerDeviceId = row.ControllerDeviceId,
        hostDeviceId = row.HostDeviceId,
        mode = row.Mode,
        transport = row.Transport,
        source = row.Source,
        relayNodeId = row.RelayNodeId,
        startedAt = row.StartedAt,
        endedAt = row.EndedAt,
        endReason = row.EndReason,
        bytesIn = row.BytesIn,
        bytesOut = row.BytesOut,
        rttMs = row.RttMs,
        lossPct = row.LossPct,
        platform = row.Platform,
        version = row.Version,
        clientSessionId = row.ClientSessionId,
        active = row.EndedAt is null,
        updatedAt = row.UpdatedAt,
    };
}
