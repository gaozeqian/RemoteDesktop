using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.Auth;

/// <summary>The names the host and the browser agree on. Constants here so a typo is a compile error, not a 401.</summary>
public static class OpsAuth
{
    /// <summary>Cookie holding the session token. HttpOnly: scripts never need it.</summary>
    public const string CookieName = "ops_session";

    /// <summary>Cookie holding the CSRF token. Readable by scripts, and that is the point of double submit.</summary>
    public const string CsrfCookieName = "ops_csrf";

    /// <summary>Header a mutating request must echo the CSRF cookie in.</summary>
    public const string CsrfHeaderName = "X-Ops-CSRF";

    /// <summary>Authentication scheme name.</summary>
    public const string Scheme = "ops-session";

    /// <summary>Needs any of the three roles; every signed-in account has one.</summary>
    public const string ViewerPolicy = "ops-viewer";

    /// <summary>Acknowledging alerts and requesting actions on sessions and devices (disconnect, revoke).</summary>
    public const string OperatorPolicy = "ops-operator";

    /// <summary>Rules, accounts, audit.</summary>
    public const string AdminPolicy = "ops-admin";
}

/// <summary>
/// Turns the session cookie into a principal by asking the store, once per request.
/// </summary>
/// <remarks>
/// No ticket cache and no sliding expiry: a revoked account or an expired row must stop working immediately, and the
/// query is a single indexed join. The cost of a lookup per request is the price of being able to say "disabled means
/// disabled".
/// </remarks>
public sealed class OpsSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    OpsStore store,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(OpsAuth.CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var user = await store
            .FindUserBySessionAsync(
                SessionToken.Hash(token),
                time.GetUtcNow().ToUnixTimeMilliseconds(),
                Context.RequestAborted)
            .ConfigureAwait(false);

        if (user is null)
        {
            return AuthenticateResult.Fail("The session is not valid.");
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
        ];

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
