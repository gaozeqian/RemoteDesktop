using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>The audit log and its integrity check.</summary>
public static class AuditEndpoints
{
    /// <summary>Maps <c>/api/audit</c>.</summary>
    public static void MapOpsAudit(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/audit").RequireAuthorization(OpsAuth.AdminPolicy);

        group.MapGet("/", async (int? limit, long? before, OpsStore store, CancellationToken cancellationToken) =>
        {
            var records = await store.ListAuditAsync(limit ?? 100, before, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                items = records.Select(record => new
                {
                    id = record.Id,
                    timestamp = record.Timestamp,
                    actor = record.Actor,
                    action = record.Action,
                    target = record.Target,
                    ip = record.Ip,
                    result = record.Result,
                }),
                nextBefore = records.Count == 0 ? (long?)null : records[^1].Timestamp,
            });
        });

        group.MapGet("/verify", async (OpsStore store, CancellationToken cancellationToken) =>
        {
            var records = await store.ReadAuditChainAsync(cancellationToken).ConfigureAwait(false);
            var verification = AuditVerifier.Verify(records);
            return Results.Ok(new
            {
                valid = verification.IsValid,
                firstInvalidId = verification.FirstInvalidId,
                checkedEntries = verification.Checked,
            });
        });
    }
}
