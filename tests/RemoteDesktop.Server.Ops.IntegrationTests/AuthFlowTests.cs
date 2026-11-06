using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The sign-in path over a real socket: status codes, cookies, CSRF and the lockout. These are the promises M0 makes,
/// and every one of them is a place where a plausible-looking refactor turns "protected" into "looks protected".
/// </summary>
[Collection("ops-console")]
public class AuthFlowTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private const string CsrfHeader = "X-Ops-CSRF";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Api_requires_a_session()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        var response = await client.GetAsync("/api/me", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_static_shell_is_served_with_the_security_headers()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        var response = await client.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("运维监控台", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task A_password_alone_does_not_sign_anyone_in()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        var login = await LoginAsync(client, "admin");

        Assert.Equal("totp-required", login.Status);
        Assert.NotNull(login.Challenge);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_totp_step_issues_a_session_that_names_the_user_and_role()
    {
        var (client, cookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (client)
        {
            var jar = cookies.GetCookies(fixture.BaseAddress);
            Assert.True(jar["ops_session"] is { HttpOnly: true }, "the session cookie must not be readable by scripts");
            Assert.True(jar["ops_csrf"] is { HttpOnly: false }, "the CSRF cookie must be readable by the page");

            var me = await client.GetFromJsonAsync<MeResponse>("/api/me", Ct);
            Assert.NotNull(me);
            Assert.Equal("admin", me.Name);
            Assert.Equal(OpsRoles.Admin, me.Role);
            Assert.False(string.IsNullOrEmpty(me.Csrf));
        }
    }

    [Fact]
    public async Task A_wrong_password_is_refused_without_revealing_whether_the_account_exists()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "not the password" }, Ct);
        var unknownUser = await client.PostAsJsonAsync("/api/auth/login", new { username = "nobody-here", password = "not the password" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
    }

    [Fact]
    public async Task A_mutating_call_without_the_csrf_header_is_refused()
    {
        var (client, cookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (client)
        {
            var withoutHeader = await client.PostAsync("/api/auth/logout", content: null, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);

            var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]!.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            request.Headers.Add(CsrfHeader, csrf);
            var withHeader = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.NoContent, withHeader.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task The_csrf_token_of_the_current_session_is_accepted()
    {
        var (signedIn, cookies) = await SignInAsync("admin", fixture.AdminSecret);
        signedIn.Dispose();
        var jar = cookies.GetCookies(fixture.BaseAddress);

        // A client with no cookie jar of its own: only what the request spells out reaches the server.
        using var bare = new HttpClient { BaseAddress = fixture.BaseAddress };
        using var request = RawLogout(jar["ops_session"]!.Value, jar["ops_csrf"]!.Value, jar["ops_csrf"]!.Value);

        Assert.Equal(HttpStatusCode.NoContent, (await bare.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_csrf_token_that_belongs_to_another_session_is_refused()
    {
        var (clientA, cookiesA) = await SignInAsync("admin", fixture.AdminSecret);
        var (clientB, cookiesB) = await SignInAsync("admin", fixture.AdminSecret);
        clientB.Dispose();
        using (clientA)
        {
            var a = cookiesA.GetCookies(fixture.BaseAddress);
            var b = cookiesB.GetCookies(fixture.BaseAddress);
            Assert.NotEqual(a["ops_csrf"]!.Value, b["ops_csrf"]!.Value);

            // Cookie and header agree with each other (so the old double-submit check passes), but the value is the
            // token another valid session was given, not this one's.
            using var bare = new HttpClient { BaseAddress = fixture.BaseAddress };
            using var request = RawLogout(a["ops_session"]!.Value, b["ops_csrf"]!.Value, b["ops_csrf"]!.Value);

            Assert.Equal(HttpStatusCode.Forbidden, (await bare.SendAsync(request, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync("/api/me", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_csrf_token_the_caller_made_up_is_refused_even_when_cookie_and_header_agree()
    {
        var (client, cookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (client)
        {
            var session = cookies.GetCookies(fixture.BaseAddress)["ops_session"]!.Value;
            var forged = SessionToken.Create();

            // What a sibling-subdomain attacker can do: write both the cookie and the header, but not read the
            // HttpOnly session cookie and so not derive the token that goes with it.
            using var bare = new HttpClient { BaseAddress = fixture.BaseAddress };
            using var request = RawLogout(session, forged, forged);

            Assert.Equal(HttpStatusCode.Forbidden, (await bare.SendAsync(request, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task The_csrf_token_must_also_match_in_the_header_and_cookie()
    {
        var (client, cookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (client)
        {
            var jar = cookies.GetCookies(fixture.BaseAddress);

            // The right token in the header only is still refused: the cookie has to agree.
            using var bare = new HttpClient { BaseAddress = fixture.BaseAddress };
            using var request = RawLogout(jar["ops_session"]!.Value, SessionToken.Create(), jar["ops_csrf"]!.Value);

            Assert.Equal(HttpStatusCode.Forbidden, (await bare.SendAsync(request, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task The_csrf_token_stops_working_once_the_session_is_signed_out()
    {
        var (client, cookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (client)
        {
            var jar = cookies.GetCookies(fixture.BaseAddress);
            var session = jar["ops_session"]!.Value;
            var csrf = jar["ops_csrf"]!.Value;

            using var bare = new HttpClient { BaseAddress = fixture.BaseAddress };
            using var first = RawLogout(session, csrf, csrf);
            Assert.Equal(HttpStatusCode.NoContent, (await bare.SendAsync(first, Ct)).StatusCode);

            using var replay = RawLogout(session, csrf, csrf);
            Assert.Equal(HttpStatusCode.Unauthorized, (await bare.SendAsync(replay, Ct)).StatusCode);
        }
    }

    /// <summary>A sign-out request whose cookies and header are spelled out by hand.</summary>
    private static HttpRequestMessage RawLogout(string session, string csrfCookie, string csrfHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", $"ops_session={session}; ops_csrf={csrfCookie}");
        request.Headers.Add(CsrfHeader, csrfHeader);
        return request;
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_out_with_a_retry_after()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = await client.PostAsJsonAsync("/api/auth/login", new { username = "lockme", password = "wrong" }, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var locked = await client.PostAsJsonAsync("/api/auth/login", new { username = "lockme", password = OpsHostFixture.Password }, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        var body = await locked.Content.ReadFromJsonAsync<LoginResponse>(Ct);
        Assert.Equal("locked", body!.Status);
        Assert.True(body.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task Respelling_the_same_name_does_not_get_a_fresh_failure_budget()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        // The lookup folds the name (trim, upper-case); the throttle used to key on the raw spelling, so every one of
        // these was a different account for the lockout but the same account for the password check.
        foreach (var spelling in new[] { "ghost", "Ghost", " ghost ", "GHOST", "ghost\t" })
        {
            var refused = await client.PostAsJsonAsync("/api/auth/login", new { username = spelling, password = "wrong" }, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var locked = await client.PostAsJsonAsync("/api/auth/login", new { username = "gHoSt", password = "wrong" }, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Wrong_second_factor_codes_are_budgeted_across_challenges()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        // A correct password starts a fresh challenge; it must not also reset the second factor's failure budget,
        // or five guesses per challenge becomes unlimited guesses for anyone who has the password.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var login = await LoginAsync(client, "totpuser");
            var refused = await client.PostAsJsonAsync(
                "/api/auth/totp",
                new { challenge = login.Challenge, code = WrongCode(fixture.TotpTestSecret) },
                Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var next = await LoginAsync(client, "totpuser");
        var locked = await client.PostAsJsonAsync(
            "/api/auth/totp",
            new { challenge = next.Challenge, code = WrongCode(fixture.TotpTestSecret) },
            Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        var body = await locked.Content.ReadFromJsonAsync<LoginResponse>(Ct);
        Assert.Equal("locked", body!.Status);
    }

    [Fact]
    public async Task An_expired_or_forged_session_cookie_is_refused()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("Cookie", "ops_session=forged-token");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A six-digit value that is not the current code and is far enough away not to match a neighbouring window.
    /// </summary>
    private string WrongCode(string secret)
    {
        var current = int.Parse(Totp.Compute(secret, fixture.Now), CultureInfo.InvariantCulture);
        return ((current + 500_000) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password = OpsHostFixture.Password }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(Ct))!;
    }

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync(string username, string secret)
    {
        var (client, cookies) = fixture.CreateClient();

        var login = await LoginAsync(client, username);
        var totp = await client.PostAsJsonAsync(
            "/api/auth/totp",
            new { challenge = login.Challenge, code = fixture.NextTotp(secret) },
            Ct);

        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);
        return (client, cookies);
    }

    /// <summary>The JSON the sign-in endpoints return, as much of it as the tests read.</summary>
    public sealed record LoginResponse(string? Status, string? Challenge, int? RetryAfterSeconds);

    /// <summary>The current-user response.</summary>
    public sealed record MeResponse(string? Name, string? Role, string? Csrf);
}
