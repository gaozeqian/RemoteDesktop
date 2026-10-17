using System.Globalization;
using System.Net.Http.Json;
using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// The device list, as one rendezvous node describes it. The console does not keep its own copy: the registry belongs
/// to the signaling server, and proxying the question is what keeps a second source of truth from appearing.
/// </summary>
public static class DeviceEndpoints
{
    /// <summary>Named client for node queries, so its timeout is configured once and it is not the scraper's.</summary>
    public const string ClientName = "ops-node";

    /// <summary>Maps <c>GET /api/devices</c>.</summary>
    public static void MapOpsDevices(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/devices", async (
            string? node,
            string? cursor,
            int? limit,
            OpsStore store,
            IHttpClientFactory httpClientFactory,
            OpsOptions options,
            CancellationToken cancellationToken) =>
        {
            var rendezvous = (await store.ListNodesAsync(cancellationToken).ConfigureAwait(false))
                .Where(candidate => candidate.Enabled && string.Equals(candidate.Role, "rendezvous", StringComparison.Ordinal))
                .ToArray();

            var target = string.IsNullOrEmpty(node)
                ? rendezvous.FirstOrDefault()
                : rendezvous.FirstOrDefault(candidate => string.Equals(candidate.Id, node, StringComparison.Ordinal));

            if (target is null)
            {
                return Results.NotFound(new { error = "node", message = "no enabled rendezvous node matches" });
            }

            var query = $"?limit={Math.Clamp(limit ?? 50, 1, 200).ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrEmpty(cursor))
            {
                query += "&cursor=" + Uri.EscapeDataString(cursor);
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                target.BaseUrl.TrimEnd('/') + "/internal/v1/devices" + query);
            if (!string.IsNullOrEmpty(options.NodeToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.NodeToken);
            }

            using var client = httpClientFactory.CreateClient(ClientName);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = "node", message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return Results.Json(
                        new { error = "node", status = (int)response.StatusCode },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                var page = await response.Content
                    .ReadFromJsonAsync<NodeDevicePage>(OpsEventJson.Options, cancellationToken)
                    .ConfigureAwait(false);

                if (page is null)
                {
                    return Results.Json(new { error = "node", message = "empty response" }, statusCode: StatusCodes.Status502BadGateway);
                }

                return Results.Ok(new
                {
                    node = new { id = target.Id, name = target.Name },
                    items = page.Devices,
                    nextCursor = page.NextCursor,
                });
            }
        }).RequireAuthorization(OpsAuth.ViewerPolicy);
    }
}
