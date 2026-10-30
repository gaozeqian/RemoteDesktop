using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>What the action form posts. Every field optional so a bad request fails on validation, not on binding.</summary>
public sealed record CreateActionRequest(string? Kind, string? NodeId, string? DeviceId, string? SessionId);

/// <summary>
/// The two-person management actions: an operator asks for something (disconnect a session, revoke a device), an
/// admin approves it, and only the approval reaches the signaling server. Both steps are audited, so a command that
/// changed something can always be read back to who asked and who approved.
/// </summary>
/// <remarks>
/// The console never reaches the device directly — it asks the rendezvous node, which is the only thing that holds a
/// signaling connection to it. <see cref="ClientName"/> is shared with the device proxy, so both node calls have one
/// timeout and one client.
/// </remarks>
public static class ActionEndpoints
{
    /// <summary>Maps <c>/api/actions</c>.</summary>
    public static void MapOpsActions(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var actions = endpoints.MapGroup("/api/actions").RequireAuthorization(OpsAuth.ViewerPolicy);

        actions.MapGet("/", async (string? state, int? limit, OpsStore store, CancellationToken cancellationToken) =>
        {
            var rows = await store.ListActionsAsync(state, limit ?? 100, cancellationToken).ConfigureAwait(false);
            return Results.Ok(rows.Select(Describe));
        });

        actions.MapPost("/", async (
            CreateActionRequest request,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var actor = context.User.Identity?.Name ?? "unknown";

            var kind = request.Kind?.Trim() switch
            {
                ActionKinds.DisconnectSession => ActionKinds.DisconnectSession,
                ActionKinds.RevokeDevice => ActionKinds.RevokeDevice,
                ActionKinds.UnrevokeDevice => ActionKinds.UnrevokeDevice,
                _ => null,
            };

            if (kind is null)
            {
                return Results.BadRequest(new { error = "kind", message = "kind must be disconnect_session, revoke_device or unrevoke_device" });
            }

            if (!uint.TryParse(request.DeviceId, NumberStyles.None, CultureInfo.InvariantCulture, out var deviceId))
            {
                return Results.BadRequest(new { error = "deviceId", message = "deviceId is not a nine-digit device code" });
            }

            if (kind == ActionKinds.DisconnectSession && string.IsNullOrWhiteSpace(request.SessionId))
            {
                return Results.BadRequest(new { error = "sessionId", message = "a disconnect needs the session id" });
            }

            var node = (await store.ListNodesAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.Enabled
                    && string.Equals(candidate.Role, "rendezvous", StringComparison.Ordinal)
                    && string.Equals(candidate.Id, request.NodeId, StringComparison.Ordinal));
            if (node is null)
            {
                return Results.BadRequest(new { error = "nodeId", message = "no enabled rendezvous node matches" });
            }

            var action = new OpsAdminActionRow(
                Guid.NewGuid().ToString("N"),
                kind,
                node.Id,
                deviceId.ToString(CultureInfo.InvariantCulture),
                kind == ActionKinds.DisconnectSession ? request.SessionId : null,
                JsonSerializer.Serialize(new { nodeName = node.Name }, OpsEventJson.Options),
                ActionStates.Pending,
                actor,
                time.GetUtcNow().ToUnixTimeMilliseconds(),
                null, null, null, null);

            await store.CreateActionAsync(action, cancellationToken).ConfigureAwait(false);
            await AuditAsync(store, context, actor, "action.request", action.Id, $"request {kind} on device {deviceId}", time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(Describe(action));
        }).RequireAuthorization(OpsAuth.OperatorPolicy);

        actions.MapPost("/{id}/approve", async (
            string id,
            HttpContext context,
            OpsStore store,
            IHttpClientFactory httpClientFactory,
            OpsOptions options,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var actor = context.User.Identity?.Name ?? "unknown";
            var at = time.GetUtcNow();

            var approved = await store.ApproveActionAsync(id, actor, at.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
            if (approved is null)
            {
                var existing = await store.FindActionAsync(id, cancellationToken).ConfigureAwait(false);
                var message = existing is not null && string.Equals(existing.RequestedBy, actor, StringComparison.Ordinal)
                    ? "the requester cannot approve their own action"
                    : "the action is not pending";
                return Results.BadRequest(new { error = "approve", message });
            }

            await AuditAsync(store, context, actor, "action.approve", approved.Id, $"approved {approved.Kind} on device {approved.DeviceId}", at, cancellationToken)
                .ConfigureAwait(false);

            // Once the row says "approved" the only way out of that state is CompleteActionAsync. A node call that
            // throws (DNS failure, timeout, connection refused) used to escape as a 500 and leave the row approved
            // forever: an emergency revoke that the UI reported as an error could never be retried, because approval
            // only accepts a pending row. Every outcome is turned into a terminal state instead.
            (string State, string? Error) outcome;
            try
            {
                outcome = await ExecuteAsync(store, httpClientFactory, options, approved, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                outcome = (ActionStates.Failed, "the node did not answer before the request timed out");
            }
            catch (OperationCanceledException)
            {
                outcome = (ActionStates.Failed, "the approval was cancelled before the node answered");
            }
            catch (Exception ex)
            {
                // Every other failure — DNS, TLS, a response over the size limit, an unexpected provider error —
                // becomes a terminal state too. The row being left approved is the one outcome that cannot be
                // retried from the UI.
                outcome = (ActionStates.Failed, $"the node call failed: {ex.Message}");
            }

            // None rather than the request's token: the row must be closed even when the browser has already gone
            // away, which is one of the ways an action used to be left approved forever.
            await store.CompleteActionAsync(approved.Id, outcome.State, time.GetUtcNow().ToUnixTimeMilliseconds(), outcome.Error, CancellationToken.None)
                .ConfigureAwait(false);
            await AuditAsync(store, context, actor, "action.execute", approved.Id, $"{outcome.State} {approved.Kind}", time.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);

            return Results.Ok(Describe((await store.FindActionAsync(approved.Id, cancellationToken).ConfigureAwait(false))!));
        }).RequireAuthorization(OpsAuth.AdminPolicy);
    }

    /// <summary>Carries the approved action to the signaling node and reports what the node answered.</summary>
    private static async Task<(string State, string? Error)> ExecuteAsync(
        OpsStore store,
        IHttpClientFactory httpClientFactory,
        OpsOptions options,
        OpsAdminActionRow action,
        CancellationToken cancellationToken)
    {
        var node = (await store.ListNodesAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.Id, action.NodeId, StringComparison.Ordinal));
        if (node is null)
        {
            return (ActionStates.Failed, "the rendezvous node is no longer registered");
        }

        if (NodeHttp.RefusalReason(node.BaseUrl, options) is { } refusal)
        {
            return (ActionStates.Failed, refusal);
        }

        var path = action.Kind == ActionKinds.DisconnectSession
            ? "/internal/v1/admin/disconnect"
            : "/internal/v1/admin/revoke";

        object body = action.Kind == ActionKinds.DisconnectSession
            ? new { deviceId = action.DeviceId, sessionId = action.SessionId }
            : new { deviceId = action.DeviceId, revoked = action.Kind == ActionKinds.RevokeDevice };

        using var request = new HttpRequestMessage(HttpMethod.Post, node.BaseUrl.TrimEnd('/') + path)
        {
            Content = JsonContent.Create(body, options: OpsEventJson.Options),
        };
        if (!string.IsNullOrEmpty(options.NodeToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.NodeToken);
        }

        using var client = httpClientFactory.CreateClient(DeviceEndpoints.ClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return (ActionStates.Executed, null);
        }

        // An error body is shown to an operator and stored in the action row. It does not need to be megabytes, and a
        // node that answers an error with an unbounded stream must not decide how much the console buffers.
        const int maxErrorBytes = 64 * 1024;
        var message = await NodeHttp
            .ReadStringBoundedAsync(response.Content, maxErrorBytes, cancellationToken)
            .ConfigureAwait(false);
        var trimmed = message.Trim();
        if (trimmed.Length > 512)
        {
            trimmed = trimmed[..512];
        }

        return (ActionStates.Failed, $"node answered HTTP {(int)response.StatusCode}: {trimmed}");
    }

    private static object Describe(OpsAdminActionRow row) => new
    {
        id = row.Id,
        kind = row.Kind,
        nodeId = row.NodeId,
        deviceId = row.DeviceId,
        sessionId = row.SessionId,
        state = row.State,
        requestedBy = row.RequestedBy,
        requestedAt = row.RequestedAt,
        approvedBy = row.ApprovedBy,
        approvedAt = row.ApprovedAt,
        executedAt = row.ExecutedAt,
        error = row.Error,
    };

    private static Task<string> AuditAsync(
        OpsStore store,
        HttpContext context,
        string actor,
        string action,
        string target,
        string detail,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                at.ToUnixTimeMilliseconds(),
                actor,
                action,
                target,
                JsonSerializer.Serialize(new { detail, ip = context.Connection.RemoteIpAddress?.ToString() }, OpsEventJson.Options),
                context.Connection.RemoteIpAddress?.ToString() ?? "-",
                "success"),
            cancellationToken);
}
