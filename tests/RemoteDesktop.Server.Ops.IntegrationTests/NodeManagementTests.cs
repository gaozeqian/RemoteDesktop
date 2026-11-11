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

    /// <summary>
    /// One token per node is what makes a leaked console credential one node's problem. The value is returned once,
    /// when it is created; the list only says one is set, because a secret a page can read again is a secret every
    /// session has to protect.
    /// </summary>
    [Fact]
    public async Task A_node_can_get_its_own_token_and_the_list_never_returns_it()
    {
        var (client, cookies) = await SignInAsync();
        using (client)
        {
            var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]!.Value;
            var name = "token-" + Guid.NewGuid().ToString("N")[..8];

            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/nodes")
            {
                Content = JsonContent.Create(new { name, role = "relay", baseUrl = "http://127.0.0.1:5083" }),
            };
            create.Headers.Add("X-Ops-CSRF", csrf);
            var created = await client.SendAsync(create, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var node = await created.Content.ReadFromJsonAsync<NodeDto>(Ct);
            Assert.False(node!.HasOwnToken);

            // An empty body asks for a generated one.
            string issued;
            using (var rotate = new HttpRequestMessage(HttpMethod.Post, $"/api/nodes/{node.Id}/token")
            {
                Content = JsonContent.Create(new { }),
            })
            {
                rotate.Headers.Add("X-Ops-CSRF", csrf);
                var response = await client.SendAsync(rotate, Ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                issued = (await response.Content.ReadFromJsonAsync<IssuedToken>(Ct))!.Token;

                // 32 random bytes, base64url: 43 characters, no padding and nothing a header would choke on.
                Assert.Equal(43, issued.Length);
                Assert.False(issued.Contains('=', StringComparison.Ordinal));
            }

            var listed = await client.GetFromJsonAsync<List<NodeDto>>("/api/nodes", Ct);
            var afterSet = Assert.Single(listed!, item => item.Id == node.Id);
            Assert.True(afterSet.HasOwnToken, "The node does not report the token that was just set on it.");

            // The token itself never comes back out of the API, not even base64-encoded inside a JSON field name.
            using (var raw = await client.GetAsync("/api/nodes", Ct))
            {
                Assert.DoesNotContain(issued, await raw.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            }

            // A value that is too short to be a secret is refused rather than stored.
            using (var shortToken = new HttpRequestMessage(HttpMethod.Post, $"/api/nodes/{node.Id}/token")
            {
                Content = JsonContent.Create(new { token = "short" }),
            })
            {
                shortToken.Headers.Add("X-Ops-CSRF", csrf);
                var refused = await client.SendAsync(shortToken, Ct);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            }

            // Clearing it puts the node back on the deployment token.
            using (var clear = new HttpRequestMessage(HttpMethod.Delete, $"/api/nodes/{node.Id}/token"))
            {
                clear.Headers.Add("X-Ops-CSRF", csrf);
                Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(clear, Ct)).StatusCode);
            }

            listed = await client.GetFromJsonAsync<List<NodeDto>>("/api/nodes", Ct);
            var afterClear = Assert.Single(listed!, item => item.Id == node.Id);
            Assert.False(afterClear.HasOwnToken, "The node still reports a token of its own after clearing it.");

            var audit = await client.GetFromJsonAsync<AuditList>("/api/audit?limit=100", Ct);
            Assert.Contains(audit!.Items, item => item.Action == "node.token.set" && item.Target == name);
            Assert.Contains(audit.Items, item => item.Action == "node.token.clear" && item.Target == name);

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

    public sealed record NodeDto(string Id, string Name, string BaseUrl, bool HasOwnToken);

    public sealed record IssuedToken(string NodeId, string Token, bool HasOwnToken);

    public sealed record LoginResponse(string? Status, string? Challenge);

    public sealed record AuditList(List<AuditEntryDto> Items);

    public sealed record AuditEntryDto(string Action, string Target);
}
