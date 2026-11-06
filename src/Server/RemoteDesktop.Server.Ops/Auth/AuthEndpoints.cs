using System.Security.Claims;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.Auth;

/// <summary>The JSON bodies the sign-in endpoints accept.</summary>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>Second factor: the challenge from <see cref="LoginRequest"/> plus the six digits.</summary>
public sealed record TotpRequest(string? Challenge, string? Code);

/// <summary>
/// Sign-in, sign-out and the current-user probe. Four endpoints, no HTML: the browser is a static page that calls them.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Maps <c>/api/auth/*</c> and <c>/api/me</c>.</summary>
    public static void MapOpsAuth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/auth");

        group.MapPost("/login", async (LoginRequest request, HttpContext context, AuthService auth, OpsOptions options, CancellationToken cancellationToken) =>
        {
            var outcome = await auth
                .SignInAsync(request.Username, request.Password, Address(context), context.Request.Headers.UserAgent.ToString(), cancellationToken)
                .ConfigureAwait(false);

            // With TOTP disabled this step already produced a session. Without the cookies the browser would be told
            // "signed in" and then 401 on every request, which makes the documented Ops:RequireTotp=false mode a
            // console nobody can use.
            if (outcome.Session is { } session)
            {
                SetSessionCookies(context, session, options);
            }

            return Results.Json(Describe(outcome), statusCode: StatusFor(outcome.Status));
        }).AllowAnonymous();

        group.MapPost("/totp", async (TotpRequest request, HttpContext context, AuthService auth, OpsOptions options, CancellationToken cancellationToken) =>
        {
            var outcome = await auth
                .CompleteTotpAsync(request.Challenge, request.Code, Address(context), context.Request.Headers.UserAgent.ToString(), cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Session is { } session)
            {
                SetSessionCookies(context, session, options);
            }

            return Results.Json(Describe(outcome), statusCode: StatusFor(outcome.Status));
        }).AllowAnonymous();

        group.MapPost("/logout", async (HttpContext context, AuthService auth, CancellationToken cancellationToken) =>
        {
            context.Request.Cookies.TryGetValue(OpsAuth.CookieName, out var token);
            await auth.SignOutAsync(token, context.User.Identity?.Name, Address(context), cancellationToken).ConfigureAwait(false);
            context.Response.Cookies.Delete(OpsAuth.CookieName, CookiePath(context));
            context.Response.Cookies.Delete(OpsAuth.CsrfCookieName, CookiePath(context));
            return Results.NoContent();
        }).RequireAuthorization();

        endpoints.MapGet("/api/me", (HttpContext context) =>
        {
            var name = context.User.Identity?.Name ?? string.Empty;
            var role = context.User.FindFirstValue(ClaimTypes.Role) ?? OpsRoles.Viewer;
            context.Request.Cookies.TryGetValue(OpsAuth.CsrfCookieName, out var csrf);
            return Results.Ok(new { name, role, csrf = csrf ?? string.Empty });
        }).RequireAuthorization();
    }

    private static object Describe(SignInOutcome outcome) => outcome.Status switch
    {
        SignInStatus.ChallengeRequired => new
        {
            status = "totp-required",
            challenge = outcome.Challenge,
            expiresAt = outcome.ExpiresAt,
        },
        SignInStatus.Succeeded => new
        {
            status = "signed-in",
            user = new { name = outcome.Session!.User.Username, role = outcome.Session.User.Role },
            csrf = outcome.Session.Csrf,
        },
        SignInStatus.Locked => new
        {
            status = "locked",
            retryAfterSeconds = (int)(outcome.RetryAfter?.TotalSeconds ?? 0),
        },
        SignInStatus.Disabled => new { status = "disabled" },
        _ => new { status = "invalid" },
    };

    private static int StatusFor(SignInStatus status) => status switch
    {
        SignInStatus.Succeeded or SignInStatus.ChallengeRequired => StatusCodes.Status200OK,
        SignInStatus.Disabled => StatusCodes.Status403Forbidden,
        SignInStatus.Locked => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status401Unauthorized,
    };

    private static void SetSessionCookies(HttpContext context, AuthSession session, OpsOptions options)
    {
        context.Response.Cookies.Append(
            OpsAuth.CookieName,
            session.Token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = options.SessionLifetime,
                IsEssential = true,
            });

        // Readable by the page on purpose: the CSRF check compares this value with a header the page sets (and with the
        // token derived from the session), and a token scripts cannot read is a token they cannot send.
        context.Response.Cookies.Append(
            OpsAuth.CsrfCookieName,
            session.Csrf,
            new CookieOptions
            {
                HttpOnly = false,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = options.SessionLifetime,
                IsEssential = true,
            });
    }

    private static CookieOptions CookiePath(HttpContext context) => new()
    {
        Path = "/",
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
    };

    private static string? Address(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();
}
