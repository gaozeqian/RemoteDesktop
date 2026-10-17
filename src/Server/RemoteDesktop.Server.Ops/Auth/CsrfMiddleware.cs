using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace RemoteDesktop.Server.Ops.Auth;

/// <summary>
/// The second half of the double-submit check: a mutating <c>/api</c> request from a signed-in browser must echo the
/// CSRF cookie in a header. The cookie alone is not enough to forge a request, and a cross-site form cannot set a
/// custom header.
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

            if (!Matches(cookie, header))
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

    private static bool Matches(string? cookie, string? header)
    {
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(cookie);
        var right = Encoding.UTF8.GetBytes(header);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
