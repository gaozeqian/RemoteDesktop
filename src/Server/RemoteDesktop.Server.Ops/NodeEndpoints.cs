using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>What the node form posts.</summary>
public sealed record CreateNodeRequest(string? Name, string? Role, string? BaseUrl, string? Region);

/// <summary>The one field the node table can change.</summary>
public sealed record UpdateNodeRequest(bool? Enabled);

/// <summary>
/// Node registration and the scrape status the overview shows.
/// </summary>
/// <remarks>
/// Registration is manual by design. Automatic discovery would mean a node can appear in the console without anyone
/// deciding it should be watched — and "who is being monitored" is a decision, not a broadcast.
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
            OpsStore store,
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

            if (!TryNormalizeBaseUrl(request.BaseUrl, out var baseUrl))
            {
                return Results.BadRequest(new { error = "baseUrl", message = "baseUrl must be an absolute http(s) URL" });
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
            return Results.Created($"/api/nodes/{node.Id}", Describe(node));
        }).RequireAuthorization(OpsAuth.AdminPolicy);

        group.MapPatch("/{id}", async (string id, UpdateNodeRequest request, OpsStore store, CancellationToken cancellationToken) =>
        {
            if (request.Enabled is not { } enabled)
            {
                return Results.BadRequest(new { error = "enabled", message = "enabled is required" });
            }

            return await store.SetNodeEnabledAsync(id, enabled, cancellationToken).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        }).RequireAuthorization(OpsAuth.AdminPolicy);

        group.MapDelete("/{id}", async (string id, OpsStore store, CancellationToken cancellationToken) =>
            await store.DeleteNodeAsync(id, cancellationToken).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound()).RequireAuthorization(OpsAuth.AdminPolicy);
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

    private static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }
}
