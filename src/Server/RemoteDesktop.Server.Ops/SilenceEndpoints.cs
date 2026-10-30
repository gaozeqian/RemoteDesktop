using System.Text.Json;
using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>What the silence form posts. <paramref name="Hours"/> defaults to one hour.</summary>
public sealed record CreateSilenceRequest(string? RuleId, string? Node, int? Hours, string? Reason);

/// <summary>
/// Windows in which notifications are suppressed for a rule, a node, or both. The alert keeps opening and resolving,
/// and the audit trail keeps both events; what stops is the page.
/// </summary>
public static class SilenceEndpoints
{
    /// <summary>A month. Longer than that is a change to the rule, not a silence.</summary>
    public const int MaxHours = 24 * 30;

    private const int MaxReasonLength = 200;
    private const int MaxNodeLength = 128;

    /// <summary>Maps <c>/api/silences</c>.</summary>
    public static void MapOpsSilences(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/silences").RequireAuthorization(OpsAuth.ViewerPolicy);

        group.MapGet("/", async (OpsStore store, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var rows = await store
                .ListActiveSilencesAsync(time.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(rows.Select(Describe));
        });

        group.MapPost("/", async (
            CreateSilenceRequest request,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var ruleId = string.IsNullOrWhiteSpace(request.RuleId) ? null : request.RuleId.Trim();
            var node = string.IsNullOrWhiteSpace(request.Node) ? null : request.Node.Trim();

            if (ruleId is null && node is null)
            {
                return Results.BadRequest(new { error = "scope", message = "give ruleId, node, or both" });
            }

            if (node is { Length: > MaxNodeLength })
            {
                return Results.BadRequest(new { error = "node", message = $"node is at most {MaxNodeLength} characters" });
            }

            if (ruleId is not null)
            {
                var rules = await store.ListAlertRulesAsync(cancellationToken).ConfigureAwait(false);
                if (!rules.Any(rule => string.Equals(rule.Id, ruleId, StringComparison.Ordinal)))
                {
                    // Otherwise a typo'd id silences nothing while looking like it does.
                    return Results.BadRequest(new { error = "ruleId", message = "no such rule" });
                }
            }

            var reason = request.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
            {
                return Results.BadRequest(
                    new { error = "reason", message = $"reason is required and at most {MaxReasonLength} characters" });
            }

            var hours = request.Hours ?? 1;
            if (hours is < 1 or > MaxHours)
            {
                return Results.BadRequest(new { error = "hours", message = $"hours must be between 1 and {MaxHours}" });
            }

            var now = time.GetUtcNow();
            var silence = new OpsSilenceRow(
                Guid.NewGuid().ToString("N"),
                ruleId,
                node,
                reason,
                context.User.Identity?.Name ?? "unknown",
                now.ToUnixTimeMilliseconds(),
                now.AddHours(hours).ToUnixTimeMilliseconds());

            await store.CreateSilenceAsync(silence, cancellationToken).ConfigureAwait(false);
            await AuditAsync(store, context, "silence.create", silence, now, cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/silences/{silence.Id}", Describe(silence));
        }).RequireAuthorization(OpsAuth.AdminPolicy);

        group.MapDelete("/{id}", async (
            string id,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            // Read first: the audit entry is more useful with the scope in it than with a bare id.
            var silence = await store.FindSilenceAsync(id, cancellationToken).ConfigureAwait(false);
            if (silence is null)
            {
                return Results.NotFound();
            }

            if (!await store.DeleteSilenceAsync(id, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await AuditAsync(store, context, "silence.delete", silence, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization(OpsAuth.AdminPolicy);
    }

    private static object Describe(OpsSilenceRow row) => new
    {
        id = row.Id,
        ruleId = row.RuleId,
        node = row.Node,
        reason = row.Reason,
        createdBy = row.CreatedBy,
        createdAt = row.CreatedAt,
        untilAt = row.UntilAt,
    };

    private static Task<string> AuditAsync(
        OpsStore store,
        HttpContext context,
        string action,
        OpsSilenceRow silence,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                at.ToUnixTimeMilliseconds(),
                context.User.Identity?.Name ?? "unknown",
                action,
                silence.Id,
                JsonSerializer.Serialize(
                    new
                    {
                        ip = context.Connection.RemoteIpAddress?.ToString(),
                        ruleId = silence.RuleId,
                        node = silence.Node,
                        untilAt = silence.UntilAt,
                        reason = silence.Reason,
                    },
                    OpsEventJson.Options),
                context.Connection.RemoteIpAddress?.ToString() ?? "-",
                "success"),
            cancellationToken);
}
