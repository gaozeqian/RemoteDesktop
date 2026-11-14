using System.Net;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Liveness for the console itself, database included. Loopback only: a public health endpoint on an ops box is an
/// inventory answer for anyone who asks, so requests that arrived through any reverse proxy are refused as well.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Maps <c>/health</c>.</summary>
    public static void MapOpsHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", async (HttpContext context, OpsHealthProbe probe, OpsStore store, CancellationToken cancellationToken) =>
        {
            if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
            {
                return Results.NotFound();
            }

            // A reverse proxy on this machine connects from loopback, so the socket check alone would let it expose
            // this endpoint. A forwarded header means the request came through one; the health check is for a local
            // curl or a supervisor, not for the proxy's clients.
            if (context.Request.Headers.ContainsKey("X-Forwarded-For")
                || context.Request.Headers.ContainsKey("X-Forwarded-Proto")
                || context.Request.Headers.ContainsKey("Forwarded"))
            {
                return Results.NotFound();
            }

            return await probe.IsHealthyAsync(cancellationToken).ConfigureAwait(false)
                ? Results.Ok(new { status = "healthy", database = store.Provider })
                : Results.Json(new { status = "unhealthy", database = store.Provider }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();
    }
}
