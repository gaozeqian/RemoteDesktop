using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Hosting;

/// <summary>Requires the node token the monitoring console presents on the internal API.</summary>
public static class NodeTokenEndpoint
{
    /// <summary>
    /// Rejects a request without the node token before the handler runs. The expected token is read per request rather
    /// than captured at startup so a configuration reload is honoured, and so the filter can be shared by hosts whose
    /// options types differ.
    /// </summary>
    /// <param name="builder">The endpoint to protect.</param>
    /// <param name="expectedToken">Reads the configured token from the request's services or configuration.</param>
    public static RouteHandlerBuilder RequireNodeToken(
        this RouteHandlerBuilder builder,
        Func<HttpContext, string?> expectedToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(expectedToken);

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var authorized = NodeTokenGuard.IsAuthorized(
                http.Request.Headers.Authorization.ToString(),
                expectedToken(http),
                http.Connection.RemoteIpAddress);

            return authorized ? await next(context).ConfigureAwait(false) : Results.Unauthorized();
        });
    }
}
