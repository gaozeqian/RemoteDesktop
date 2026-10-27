using System.Net;
using System.Net.Http.Json;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The node page's edit path and the audit entries that make a node change attributable. The address decides where
/// the console sends its node token, so adding and changing one is an administrative action.
/// </summary>
[Collection("ops-console")]
public class NodeManagementTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_node_address_can_be_changed_and_the_change_is_audited()
    {
        var (client, cookies) = await SignInAsync();
        using (client)
        {
            var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]!.Value;
            var name = "edit-" + Guid.NewGuid().ToString("N")[..8];

            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/nodes")
            {
                Content = JsonContent.Create(new { name, role = "relay", baseUrl = "http://127.0.0.1:5083" }),
            };
            create.Headers.Add("X-Ops-CSRF", csrf);
            var created = await client.SendAsync(create, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var node = await created.Content.ReadFromJsonAsync<NodeDto>(Ct);

            using var update = new HttpRequestMessage(HttpMethod.Patch, $"/api/nodes/{node!.Id}")
            {
                Content = JsonContent.Create(new { baseUrl = "http://127.0.0.1:5099" }),
            };
            update.Headers.Add("X-Ops-CSRF", csrf);
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(update, Ct)).StatusCode);

            var nodes = await client.GetFromJsonAsync<List<NodeDto>>("/api/nodes", Ct);
            Assert.Contains(nodes!, item => item.Id == node.Id && item.BaseUrl == "http://127.0.0.1:5099");

            var audit = await client.GetFromJsonAsync<AuditList>("/api/audit?limit=100", Ct);
            Assert.Contains(audit!.Items, item => item.Action == "node.create" && item.Target == name);
            Assert.Contains(audit.Items, item => item.Action == "node.update" && item.Target == name);

            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/nodes/{node.Id}");
            delete.Headers.Add("X-Ops-CSRF", csrf);
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete, Ct)).StatusCode);
        }
    }

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync()
    {
        var (client, cookies) = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = OpsHostFixture.Password }, Ct);
        var challenge = (await login.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.Challenge;

        var totp = await client.PostAsJsonAsync(
            "/api/auth/totp",
            new { challenge, code = fixture.NextTotp(fixture.AdminSecret) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);
        return (client, cookies);
    }

    public sealed record NodeDto(string Id, string Name, string BaseUrl);

    public sealed record LoginResponse(string? Status, string? Challenge);

    public sealed record AuditList(List<AuditEntryDto> Items);

    public sealed record AuditEntryDto(string Action, string Target);
}
