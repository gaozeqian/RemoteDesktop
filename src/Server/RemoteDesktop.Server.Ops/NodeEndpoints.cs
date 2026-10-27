using System.Text.Json;
using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>What the node form posts.</summary>
public sealed record CreateNodeRequest(string? Name, string? Role, string? BaseUrl, string? Region);

/// <summary>The fields the node table can change. Both optional so a partial edit cannot blank a column.</summary>
public sealed record UpdateNodeRequest(bool? Enabled, string? BaseUrl);

/// <summary>
/// Node registration and the scrape status the overview shows.
/// </summary>
/// <remarks>
/// Registration is manual by design. Automatic discovery would mean a node can appear in the console without anyone
/// deciding it should be watched — and "who is being monitored" is a decision, not a broadcast. Every change is
/// audited: the address decides where the console sends its node token, and deleting a node deletes its history.
/// </remarks>
public static class NodeEndpoints
{
    private const int MaxNameLength = 64;

    /// <summary>Maps <c>/api/nodes</c>.</summary>
    public static void MapOpsNodes(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/nodes").RequireAuthorization(OpsAuth.ViewerPolicy);

        group.MapGet("/", async (OpsStore store, CancellationToken cancellationToken) =>
        {
            var nodes = await store.ListNodesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(nodes.Select(Describe));
        });

        group.MapPost("/", async (
            CreateNodeRequest request,
            HttpContext context,
            OpsStore store,
            OpsOptions options,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                return Results.BadRequest(new { error = "name", message = $"name is required and at most {MaxNameLength} characters" });
            }

            var role = CanonicalRole(request.Role);
            if (role is null)
            {
                return Results.BadRequest(new { error = "role", message = "role must be rendezvous or relay" });
            }

            if (!TryNormalizeBaseUrl(request.BaseUrl, out var baseUrl, out var uri))
            {
                return Results.BadRequest(new { error = "baseUrl", message = "baseUrl must be an absolute http(s) URL" });
            }

            if (TokenWouldTravelInClear(uri, options, out var clearWarning))
            {
                return Results.BadRequest(new { error = "baseUrl", message = clearWarning });
            }

            var nodes = await store.ListNodesAsync(cancellationToken).ConfigureAwait(false);
            if (nodes.Any(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Conflict(new { error = "name", message = "a node with that name already exists" });
            }

            var node = new OpsNode(
                Guid.NewGuid().ToString("N"),
                name,
                role,
                baseUrl,
                string.IsNullOrWhiteSpace(request.Region) ? null : request.Region.Trim(),
                Enabled: true,
                LastScrapeAt: null,
                LastScrapeOk: null,
                time.GetUtcNow().ToUnixTimeMilliseconds());

            await store.CreateNodeAsync(node, cancellationToken).ConfigureAwait(false);
            await AuditAsync(store, context, "node.create", node.Name, $"registered {role} at {baseUrl}", time, cancellationToken)
                .ConfigureAwait(false);
            return Results.Created($"/api/nodes/{node.Id}", Describe(node));
        }).RequireAuthorization(OpsAuth.AdminPolicy);

        group.MapPatch("/{id}", async (
            string id,
            UpdateNodeRequest request,
            HttpContext context,
            OpsStore store,
            OpsOptions options,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (request.Enabled is null && request.BaseUrl is null)
            {
                return Results.BadRequest(new { error = "request", message = "provided enabled and/or baseUrl" });
            }

            var node = await store.FindNodeAsync(id, cancellationToken).ConfigureAwait(false);
            if (node is null)
            {
                return Results.NotFound();
            }

            string? baseUrl = null;
            if (request.BaseUrl is not null)
            {
                if (!TryNormalizeBaseUrl(request.BaseUrl, out var normalized, out var uri))
                {
                    return Results.BadRequest(new { error = "baseUrl", message = "baseUrl must be an absolute http(s) URL" });
                }

                if (TokenWouldTravelInClear(uri, options, out var clearWarning))
                {
                    return Results.BadRequest(new { error = "baseUrl", message = clearWarning });
                }

                baseUrl = normalized;
            }

            if (!await store.UpdateNodeAsync(id, request.Enabled, baseUrl, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            var changes = new List<string>(2);
            if (request.Enabled is { } enabled)
            {
                changes.Add(enabled ? "enabled" : "disabled");
            }

            if (baseUrl is not null)
            {
                changes.Add($"address {node.BaseUrl} -> {baseUrl}");
            }

            await AuditAsync(store, context, "node.update", node.Name, string.Join("; ", changes), time, cancellationToken)
                .ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization(OpsAuth.AdminPolicy);

        group.MapDelete("/{id}", async (
            string id,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var node = await store.FindNodeAsync(id, cancellationToken).ConfigureAwait(false);
            if (node is null)
            {
                return Results.NotFound();
            }

            if (!await store.DeleteNodeAsync(id, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await AuditAsync(store, context, "node.delete", node.Name, $"deleted {node.Role} at {node.BaseUrl}", time, cancellationToken)
                .ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization(OpsAuth.AdminPolicy);
    }

    private static object Describe(OpsNode node) => new
    {
        id = node.Id,
        name = node.Name,
        role = node.Role,
        baseUrl = node.BaseUrl,
        region = node.Region,
        enabled = node.Enabled,
        lastScrapeAt = node.LastScrapeAt,
        lastScrapeOk = node.LastScrapeOk,
    };

    private static string? CanonicalRole(string? role)
    {
        if (string.Equals(role?.Trim(), "rendezvous", StringComparison.OrdinalIgnoreCase))
        {
            return "rendezvous";
        }

        return string.Equals(role?.Trim(), "relay", StringComparison.OrdinalIgnoreCase) ? "relay" : null;
    }

    private static bool TryNormalizeBaseUrl(string? value, out string normalized, out Uri? uri)
    {
        normalized = string.Empty;
        uri = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = parsed;
        normalized = parsed.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>
    /// Refuses an http node when a node token is configured. The token is a bearer credential; sending it to a
    /// non-loopback address in the clear hands it to anyone on the path. Loopback http is allowed because it never
    /// leaves the machine.
    /// </summary>
    private static bool TokenWouldTravelInClear(Uri? uri, OpsOptions options, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrEmpty(options.NodeToken)
            || uri is null
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.IsLoopback)
        {
            return false;
        }

        message = "an http baseUrl would send Ops:NodeToken in the clear; use https or a loopback address";
        return true;
    }

    private static Task<string> AuditAsync(
        OpsStore store,
        HttpContext context,
        string action,
        string target,
        string detail,
        TimeProvider time,
        CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                time.GetUtcNow().ToUnixTimeMilliseconds(),
                context.User.Identity?.Name ?? "unknown",
                action,
                target,
                JsonSerializer.Serialize(new { detail, ip = context.Connection.RemoteIpAddress?.ToString() }, OpsEventJson.Options),
                context.Connection.RemoteIpAddress?.ToString() ?? "-",
                "success"),
            cancellationToken);
}
