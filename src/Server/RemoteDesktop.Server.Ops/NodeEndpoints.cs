using System.Security.Cryptography;
using System.Text.Json;
using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>What the node form posts. <paramref name="Token"/> is optional: without it the node uses the deployment token.</summary>
public sealed record CreateNodeRequest(string? Name, string? Role, string? BaseUrl, string? Region, string? Token);

/// <summary>
/// A token for one node. An empty <paramref name="Token"/> asks the console to generate one.
/// </summary>
/// <remarks>
/// The value may be handed back to the operator here and only here; there is no read-back endpoint, because a secret
/// that can be read again is a secret the console has to protect from every session that can read a page.
/// </remarks>
public sealed record RotateNodeTokenRequest(string? Token);

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

        group.MapGet("/{id}/detail", async (
            string id,
            OpsStore store,
            IHttpClientFactory httpClientFactory,
            OpsOptions options,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var node = await store.FindNodeAsync(id, cancellationToken).ConfigureAwait(false);
            if (node is null)
            {
                return Results.NotFound();
            }

            var (info, infoError) = await FetchInfoAsync(node, httpClientFactory, options, cancellationToken).ConfigureAwait(false);
            var metrics = await store.ReadLatestForNodeAsync(node.Id, cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();

            return Results.Ok(new
            {
                node = Describe(node),
                info = info is null
                    ? null
                    : new
                    {
                        info.Name,
                        info.Role,
                        info.Version,
                        info.StartedAtUnixMilliseconds,
                        uptimeSeconds = Math.Max(0, (long)(now - DateTimeOffset.FromUnixTimeMilliseconds(info.StartedAtUnixMilliseconds)).TotalSeconds),
                    },
                infoError,
                metrics = metrics.Select(point => new { point.Metric, point.Timestamp, point.Value }),
            });
        });

        // The rule form needs a closed vocabulary of what this deployment scrapes. The catalog is filled by every
        // successful scrape, so an empty list honestly means "no node has answered yet".
        endpoints.MapGet("/api/metrics", async (OpsStore store, CancellationToken cancellationToken) =>
            Results.Ok((await store.ListMetricCatalogAsync(cancellationToken).ConfigureAwait(false))
                .Select(row => new { metric = row.Metric, kind = row.Kind })))
            .RequireAuthorization(OpsAuth.ViewerPolicy);

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

            if (!TryNormalizeBaseUrl(request.BaseUrl, out var baseUrl, out _))
            {
                return Results.BadRequest(new { error = "baseUrl", message = "baseUrl must be an absolute http(s) URL" });
            }

            var token = string.IsNullOrWhiteSpace(request.Token) ? null : request.Token.Trim();
            if (token is not null && !IsUsableToken(token))
            {
                return Results.BadRequest(new { error = "token", message = TokenRequirements });
            }

            if (NodeHttp.RefusalReason(baseUrl, token ?? options.NodeToken, options) is { } refusal)
            {
                return Results.BadRequest(new { error = "baseUrl", message = refusal });
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
                time.GetUtcNow().ToUnixTimeMilliseconds(),
                Token: token);

            try
            {
                await store.CreateNodeAsync(node, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (OpsStore.IsUniqueViolation(ex))
            {
                // Another request took the name between the check above and this insert.
                return Results.Conflict(new { error = "name", message = "a node with that name already exists" });
            }

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
                if (!TryNormalizeBaseUrl(request.BaseUrl, out var normalized, out _))
                {
                    return Results.BadRequest(new { error = "baseUrl", message = "baseUrl must be an absolute http(s) URL" });
                }

                // Checked against the token this node actually uses: the deployment's, or its own.
                if (NodeHttp.RefusalReason(normalized, NodeHttp.TokenFor(node, options), options) is { } refusal)
                {
                    return Results.BadRequest(new { error = "baseUrl", message = refusal });
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

        // The only response that ever carries a node's token. Generated when the request leaves it empty; checked
        // against the node's address first, because rotating a secret onto a plain-http public address would just be a
        // slower way of publishing it.
        group.MapPost("/{id}/token", async (
            string id,
            RotateNodeTokenRequest request,
            HttpContext context,
            OpsStore store,
            OpsOptions options,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var node = await store.FindNodeAsync(id, cancellationToken).ConfigureAwait(false);
            if (node is null)
            {
                return Results.NotFound();
            }

            var supplied = string.IsNullOrWhiteSpace(request.Token) ? null : request.Token.Trim();
            if (supplied is not null && !IsUsableToken(supplied))
            {
                return Results.BadRequest(new { error = "token", message = TokenRequirements });
            }

            var token = supplied ?? NewToken();
            if (NodeHttp.RefusalReason(node.BaseUrl, token, options) is { } refusal)
            {
                return Results.BadRequest(new { error = "baseUrl", message = refusal });
            }

            if (!await store.SetNodeTokenAsync(id, token, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await AuditAsync(
                store,
                context,
                "node.token.set",
                node.Name,
                supplied is null ? "generated a token for this node" : "set a token for this node",
                time,
                cancellationToken).ConfigureAwait(false);

            return Results.Ok(new { nodeId = id, token, hasOwnToken = true });
        }).RequireAuthorization(OpsAuth.AdminPolicy);

        // Clears the node's own token; it goes back to the deployment's, which is what an operator wants when a node
        // is being retired from separate credentials rather than deleted.
        group.MapDelete("/{id}/token", async (
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

            if (!await store.SetNodeTokenAsync(id, null, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await AuditAsync(store, context, "node.token.clear", node.Name, "falls back to Ops:NodeToken", time, cancellationToken)
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

        // Whether the node has its own secret, never the secret: the value is returned once, when it is created.
        hasOwnToken = !string.IsNullOrEmpty(node.Token),
    };

    private static async Task<(NodeInfo? Info, string? Error)> FetchInfoAsync(
        OpsNode node,
        IHttpClientFactory httpClientFactory,
        OpsOptions options,
        CancellationToken cancellationToken)
    {
        var token = NodeHttp.TokenFor(node, options);
        if (NodeHttp.RefusalReason(node.BaseUrl, token, options) is { } refusal)
        {
            return (null, refusal);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, node.BaseUrl.TrimEnd('/') + "/internal/v1/info");
        NodeHttp.ApplyAuthorization(request, token);

        using var client = httpClientFactory.CreateClient(DeviceEndpoints.ClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, ex is TaskCanceledException ? "timed out" : ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)response.StatusCode}");
            }

            try
            {
                var body = await NodeHttp.ReadBoundedAsync(response.Content, NodeHttp.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                return (JsonSerializer.Deserialize<NodeInfo>(body, OpsEventJson.Options), null);
            }
            catch (Exception ex) when (ex is JsonException or IOException or NodeResponseTooLargeException or HttpRequestException or TaskCanceledException)
            {
                return (null, ex is TaskCanceledException ? "timed out" : ex.Message);
            }
        }
    }

    /// <summary>
    /// What a token has to look like. Printable ASCII with no spaces keeps it a valid header value; 16 characters is
    /// the shortest anything here will accept, and the console generates 43.
    /// </summary>
    private const string TokenRequirements =
        "token must be 16 to 512 printable ASCII characters with no spaces";

    private static bool IsUsableToken(string token) =>
        token.Length is >= 16 and <= 512
        && token.All(character => character is >= '!' and <= '~');

    /// <summary>32 random bytes, base64url without padding: 43 characters, no header-hostile symbols.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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
