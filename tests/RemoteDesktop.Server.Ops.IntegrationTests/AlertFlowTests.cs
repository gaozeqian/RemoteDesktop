using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// A rule, a failing node, an alert, an acknowledgement, and a recovery. This is the first test where the console does
/// something rather than show something, so it walks the whole loop.
/// </summary>
[Collection("ops-console")]
public class AlertFlowTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<string> _nodes = [];
    private readonly List<string> _rules = [];

    /// <summary>Nothing to set up per test.</summary>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Removes the node and rule this test created.</summary>
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        var store = fixture.Services.GetRequiredService<OpsStore>();
        foreach (var id in _nodes)
        {
            await store.DeleteNodeAsync(id, TestContext.Current.CancellationToken);
        }

        foreach (var id in _rules)
        {
            await store.DeleteAlertRuleAsync(id, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_failing_node_opens_an_alert_that_can_be_acknowledged_and_resolves_when_the_node_is_disabled()
    {
        var (client, cookies) = await SignInAsync();
        using (client)
        {
            var ruleId = await CreateRuleAsync(client, cookies);
            var nodeId = await CreateFailingNodeAsync(client, cookies);

            var scraper = fixture.Services.GetRequiredService<MetricScraper>();
            await scraper.RunOnceAsync(Ct);

            var evaluation = fixture.Services.GetRequiredService<AlertEvaluationService>();
            await evaluation.RunOnceAsync(Ct);

            var alerts = await client.GetFromJsonAsync<List<AlertDto>>("/api/alerts?limit=50", Ct);
            var alert = Assert.Single(alerts!, item => item.RuleId == ruleId && item.Active);
            Assert.Equal("P2", alert.Severity);

            var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]!.Value;
            using (var ack = new HttpRequestMessage(HttpMethod.Post, $"/api/alerts/{alert.Id}/ack"))
            {
                ack.Headers.Add("X-Ops-CSRF", csrf);
                Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(ack, Ct)).StatusCode);
            }

            var acknowledged = Assert.Single((await client.GetFromJsonAsync<List<AlertDto>>("/api/alerts?limit=50", Ct))!, item => item.Id == alert.Id);
            Assert.Equal("admin", acknowledged.AckedBy);

            await SetNodeEnabledAsync(client, csrf, nodeId, enabled: false);
            await evaluation.RunOnceAsync(Ct);

            var resolved = Assert.Single((await client.GetFromJsonAsync<List<AlertDto>>("/api/alerts?limit=50", Ct))!, item => item.Id == alert.Id);
            Assert.False(resolved.Active);
            Assert.NotNull(resolved.ResolvedAt);
        }
    }

    [Fact]
    public async Task The_audit_log_records_the_acknowledgement_and_its_chain_verifies()
    {
        var (client, cookies) = await SignInAsync();
        using (client)
        {
            var verification = await client.GetFromJsonAsync<VerifyDto>("/api/audit/verify", Ct);

            Assert.NotNull(verification);
            Assert.True(verification.Valid, $"the chain must be sound, first bad entry: {verification.FirstInvalidId}");
            Assert.True(verification.CheckedEntries > 0);

            var audit = await client.GetFromJsonAsync<AuditList>("/api/audit?limit=50", Ct);
            Assert.Contains(audit!.Items, item => item.Action == "auth.login");
        }
    }

    private async Task<string> CreateRuleAsync(HttpClient client, CookieContainer cookies)
    {
        var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]!.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/settings/rules")
        {
            Content = JsonContent.Create(new
            {
                name = "测试：抓取失败",
                kind = "scrape",
                comparator = "gt",
                threshold = 0.5,
                forSeconds = 0,
                severity = "P2",
                enabled = true,
            }),
        };
        request.Headers.Add("X-Ops-CSRF", csrf);

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rule = await response.Content.ReadFromJsonAsync<RuleDto>(Ct);
        _rules.Add(rule!.Id);
        return rule.Id;
    }

    private async Task<string> CreateFailingNodeAsync(HttpClient client, CookieContainer cookies)
    {
        var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]!.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/nodes")
        {
            // Port 1 on loopback refuses instantly, which is the scrape failure the rule is looking for.
            Content = JsonContent.Create(new { name = "failing-" + Guid.NewGuid().ToString("N")[..8], role = "relay", baseUrl = "http://127.0.0.1:1" }),
        };
        request.Headers.Add("X-Ops-CSRF", csrf);

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var node = await response.Content.ReadFromJsonAsync<NodeDto>(Ct);
        _nodes.Add(node!.Id);
        return node.Id;
    }

    private static async Task SetNodeEnabledAsync(HttpClient client, string csrf, string nodeId, bool enabled)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/nodes/{nodeId}")
        {
            Content = JsonContent.Create(new { enabled }),
        };
        request.Headers.Add("X-Ops-CSRF", csrf);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request, Ct)).StatusCode);
    }

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync()
    {
        var (client, cookies) = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = OpsHostFixture.Password }, Ct);
        var challenge = (await login.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.Challenge;

        var totp = await client.PostAsJsonAsync(
            "/api/auth/totp",
            new { challenge, code = Totp.Compute(fixture.AdminSecret, DateTimeOffset.UtcNow) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);

        return (client, cookies);
    }

    /// <summary>One alert row.</summary>
    public sealed record AlertDto(string Id, string RuleId, string Severity, bool Active, string? AckedBy, long? ResolvedAt);

    /// <summary>One rule as the API returns it.</summary>
    public sealed record RuleDto(string Id, string Name);

    /// <summary>One node as the API returns it.</summary>
    public sealed record NodeDto(string Id, string Name);

    /// <summary>The sign-in response.</summary>
    public sealed record LoginResponse(string? Status, string? Challenge);

    /// <summary>The chain verification response.</summary>
    public sealed record VerifyDto(bool Valid, string? FirstInvalidId, int CheckedEntries);

    /// <summary>A page of audit entries.</summary>
    public sealed record AuditList(List<AuditEntryDto> Items);

    /// <summary>One audit entry as the API returns it.</summary>
    public sealed record AuditEntryDto(string Id, long Timestamp, string Actor, string Action, string Target, string Ip, string Result);
}
