using System.Net;
using System.Net.Http.Json;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// Account management over the real HTTP surface: create, reset, disable, delete, and the replay rule that makes a
/// TOTP code single-use. Before this existed the only way to revoke a compromised administrator was a database editor.
/// </summary>
[Collection("ops-console")]
public class AccountManagementTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private const string CsrfHeader = "X-Ops-CSRF";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_admin_can_create_reset_disable_and_delete_an_account()
    {
        var (admin, adminCookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (admin)
        {
            var username = "account-" + Guid.NewGuid().ToString("N")[..8];
            var created = await CreateUserAsync(admin, adminCookies, username, "initial password 123", "viewer");
            Assert.Equal(username, created.Username);

            var (user, userCookies) = await SignInAsync(username, created.TotpSecret, "initial password 123");
            using (user)
            {
                Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/me", Ct)).StatusCode);

                var csrf = adminCookies.GetCookies(fixture.BaseAddress)[OpsAuthCsrf()]!.Value;

                // Resetting the password ends the sessions the old password created.
                Assert.Equal(
                    HttpStatusCode.NoContent,
                    (await PostAsync(admin, $"/api/users/{created.Id}/password", new { password = "replacement password 456" }, csrf)).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/me", Ct)).StatusCode);

                var (again, againCookies) = await SignInAsync(username, created.TotpSecret, "replacement password 456");
                using (again)
                {
                    var disableCsrf = adminCookies.GetCookies(fixture.BaseAddress)[OpsAuthCsrf()]!.Value;
                    Assert.Equal(
                        HttpStatusCode.NoContent,
                        (await PostAsync(admin, $"/api/users/{created.Id}/disabled", new { disabled = true }, disableCsrf)).StatusCode);
                    Assert.Equal(HttpStatusCode.Unauthorized, (await again.GetAsync("/api/me", Ct)).StatusCode);

                    // A fresh client: signing in from a client that already holds an admin session would be a
                    // mutating request from an authenticated browser and is refused by CSRF before it reaches auth.
                    var (disabledClient, _) = fixture.CreateClient();
                    using (disabledClient)
                    {
                        var login = await disabledClient.PostAsJsonAsync("/api/auth/login", new { username, password = "replacement password 456" }, Ct);
                        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
                    }

                    var enableCsrf = adminCookies.GetCookies(fixture.BaseAddress)[OpsAuthCsrf()]!.Value;
                    Assert.Equal(
                        HttpStatusCode.NoContent,
                        (await PostAsync(admin, $"/api/users/{created.Id}/disabled", new { disabled = false }, enableCsrf)).StatusCode);

                    var deleteCsrf = adminCookies.GetCookies(fixture.BaseAddress)[OpsAuthCsrf()]!.Value;
                    using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{created.Id}");
                    delete.Headers.Add(CsrfHeader, deleteCsrf);
                    Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(delete, Ct)).StatusCode);

                    var (deletedClient, _) = fixture.CreateClient();
                    using (deletedClient)
                    {
                        var afterDelete = await deletedClient.PostAsJsonAsync("/api/auth/login", new { username, password = "replacement password 456" }, Ct);
                        Assert.Equal(HttpStatusCode.Unauthorized, afterDelete.StatusCode);
                    }
                }
            }

            var audit = await admin.GetFromJsonAsync<AuditList>("/api/audit?limit=100", Ct);
            Assert.Contains(audit!.Items, item => item.Action == "admin.create" && item.Target == username);
            Assert.Contains(audit.Items, item => item.Action == "admin.delete" && item.Target == username);
        }
    }

    [Fact]
    public async Task An_administrator_cannot_disable_or_delete_their_own_account()
    {
        var (admin, cookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (admin)
        {
            var users = await admin.GetFromJsonAsync<List<UserDto>>("/api/users", Ct);
            var self = Assert.Single(users!, user => user.Username == "admin");
            var csrf = cookies.GetCookies(fixture.BaseAddress)[OpsAuthCsrf()]!.Value;

            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await PostAsync(admin, $"/api/users/{self.Id}/disabled", new { disabled = true }, csrf)).StatusCode);

            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{self.Id}");
            delete.Headers.Add(CsrfHeader, csrf);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(delete, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_totp_code_that_already_signed_in_is_refused()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = OpsHostFixture.Password }, Ct);
        var challenge = (await login.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.Challenge;
        var code = fixture.NextTotp(fixture.AdminSecret);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/totp", new { challenge, code }, Ct)).StatusCode);

        // A fresh client without the session the first code created. (A POST from the signed-in client would be a
        // mutating request from an authenticated browser and would be refused by CSRF first.)
        var (secondHttp, _) = fixture.CreateClient();
        using var secondClient = secondHttp;
        var second = await secondClient.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = OpsHostFixture.Password }, Ct);
        var secondChallenge = (await second.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.Challenge;

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await secondClient.PostAsJsonAsync("/api/auth/totp", new { challenge = secondChallenge, code }, Ct)).StatusCode);
    }

    private static string OpsAuthCsrf() => "ops_csrf";

    private async Task<CreatedUser> CreateUserAsync(HttpClient admin, CookieContainer cookies, string username, string password, string role)
    {
        var csrf = cookies.GetCookies(fixture.BaseAddress)[OpsAuthCsrf()]!.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Content = JsonContent.Create(new { username, password, role }),
        };
        request.Headers.Add(CsrfHeader, csrf);

        var response = await admin.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedUser>(Ct))!;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(CsrfHeader, csrf);
        return await client.SendAsync(request, Ct);
    }

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync(
        string username,
        string secret,
        string password = OpsHostFixture.Password)
    {
        var (client, cookies) = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var challenge = (await login.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.Challenge;

        var totp = await client.PostAsJsonAsync(
            "/api/auth/totp",
            new { challenge, code = fixture.NextTotp(secret) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);
        return (client, cookies);
    }

    public sealed record CreatedUser(string Id, string Username, string Role, string TotpSecret);

    public sealed record UserDto(string Id, string Username, string Role, bool Disabled);

    public sealed record LoginResponse(string? Status, string? Challenge);

    public sealed record AuditList(List<AuditEntryDto> Items);

    public sealed record AuditEntryDto(string Action, string Target);
}
