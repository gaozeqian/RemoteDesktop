using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Auth;

/// <summary>
/// A mutating <c>/api</c> request from a signed-in browser must echo the CSRF cookie in a header, and that value must be
/// the token derived from the request's own session (<see cref="SessionToken.CsrfFor"/>). The echo keeps the browser
/// side trivial and a cross-site form cannot set a custom header; the derivation means a party that can only write
/// cookies cannot invent a pair that passes, because it cannot read the HttpOnly session cookie the token comes from.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    /// <summary>Runs the check for authenticated, state-changing API calls and passes everything else through.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (RequiresProof(context))
        {
            var cookie = context.Request.Cookies[OpsAuth.CsrfCookieName];
            var header = context.Request.Headers[OpsAuth.CsrfHeaderName].ToString();
            var session = context.Request.Cookies[OpsAuth.CookieName];

            if (string.IsNullOrEmpty(session)
                || !Matches(cookie, header)
                || !Matches(SessionToken.CsrfFor(session), header))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response
                    .WriteAsJsonAsync(new { error = "csrf" }, context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool RequiresProof(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && context.User.Identity?.IsAuthenticated == true
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method);

    private static bool Matches(string? expected, string? header)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(header))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(header);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
