using System.Data.Common;
using System.Net;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Liveness for the console itself, database included. Loopback only: a public health endpoint on an ops box is an
/// inventory answer for anyone who asks, and the reverse proxy can expose it deliberately if a load balancer needs it.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Maps <c>/health</c>.</summary>
    public static void MapOpsHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", async (HttpContext context, OpsStore store, CancellationToken cancellationToken) =>
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

            try
            {
                await store.ProbeAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(new { status = "healthy", database = store.Provider });
            }
            catch (DbException)
            {
                return Results.Json(new { status = "unhealthy", database = store.Provider }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();
    }
}
