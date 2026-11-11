using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The two-person management flow end to end: an operator asks, an admin approves, and only the approval reaches a
/// (fake) signaling node. The fake node is a real HTTP server because the thing under test is that the console calls
/// the node with the approved intent and reports what the node answered.
/// </summary>
[Collection("ops-console")]
public class ActionFlowTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ConcurrentQueue<(string Path, string Body)> _delivered = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Removes the nodes this class registered, leaving the shared console as it found it.</summary>
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        var store = fixture.Services.GetRequiredService<OpsStore>();
        foreach (var node in await store.ListNodesAsync(Ct))
        {
            await store.DeleteNodeAsync(node.Id, Ct);
        }
    }

    [Fact]
    public async Task A_disconnect_requires_a_second_person_and_then_reaches_the_node()
    {
        await using var node = await FakeRendezvous.StartAsync(_delivered);
        var nodeId = await RegisterNodeAsync(node.BaseUrl, "rdvz-review");

        var sessionId = Guid.NewGuid().ToString("N");
        var (operatorClient, operatorCookies) = await SignInAsync("operator", fixture.OperatorSecret);
        string actionId;
        using (operatorClient)
        {
            var created = await PostWithCsrfAsync(operatorClient, operatorCookies, "/api/actions", new
            {
                kind = "disconnect_session",
                nodeId,
                deviceId = "582441907",
                sessionId,
            });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var action = await created.Content.ReadFromJsonAsync<ActionDto>(Ct);

            Assert.Equal("pending", action!.State);
            Assert.Equal("operator", action.RequestedBy);
            actionId = action.Id;
        }

        var (adminClient, adminCookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (adminClient)
        {
            var approved = await PostWithCsrfAsync(adminClient, adminCookies, $"/api/actions/{actionId}/approve", null);
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            var result = await approved.Content.ReadFromJsonAsync<ActionDto>(Ct);

            Assert.Equal("executed", result!.State);
            Assert.Equal("admin", result.ApprovedBy);
        }

        var (path, body) = Assert.Single(_delivered);
        Assert.Equal("/internal/v1/admin/disconnect", path);
        Assert.Contains("582441907", body, StringComparison.Ordinal);
        Assert.Contains(sessionId, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revoke_is_approved_and_reaches_the_node()
    {
        await using var node = await FakeRendezvous.StartAsync(_delivered);
        var nodeId = await RegisterNodeAsync(node.BaseUrl, "rdvz-revoke");

        var (operatorClient, operatorCookies) = await SignInAsync("operator", fixture.OperatorSecret);
        string actionId;
        using (operatorClient)
        {
            var created = await PostWithCsrfAsync(operatorClient, operatorCookies, "/api/actions", new
            {
                kind = "revoke_device",
                nodeId,
                deviceId = "771039552",
            });
            var action = await created.Content.ReadFromJsonAsync<ActionDto>(Ct);
            actionId = action!.Id;
        }

        var (adminClient, adminCookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (adminClient)
        {
            var approved = await PostWithCsrfAsync(adminClient, adminCookies, $"/api/actions/{actionId}/approve", null);
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            var result = await approved.Content.ReadFromJsonAsync<ActionDto>(Ct);
            Assert.Equal("executed", result!.State);
        }

        var (path, body) = Assert.Single(_delivered);
        Assert.Equal("/internal/v1/admin/revoke", path);
        Assert.Contains("771039552", body, StringComparison.Ordinal);
        Assert.Contains("\"revoked\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_requester_cannot_approve_their_own_action()
    {
        await using var node = await FakeRendezvous.StartAsync(_delivered);
        var nodeId = await RegisterNodeAsync(node.BaseUrl, "rdvz-self");

        var (adminClient, adminCookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (adminClient)
        {
            // An admin may ask (operator policy includes admin), but may not be the one who approves.
            var created = await PostWithCsrfAsync(adminClient, adminCookies, "/api/actions", new
            {
                kind = "unrevoke_device",
                nodeId,
                deviceId = "123456789",
            });
            var action = await created.Content.ReadFromJsonAsync<ActionDto>(Ct);

            var selfApprove = await PostWithCsrfAsync(adminClient, adminCookies, $"/api/actions/{action!.Id}/approve", null);
            Assert.Equal(HttpStatusCode.BadRequest, selfApprove.StatusCode);
        }
    }

    [Fact]
    public async Task The_execute_audit_entry_says_success_when_the_node_carried_the_action_out()
    {
        await using var node = await FakeRendezvous.StartAsync(_delivered);
        var nodeId = await RegisterNodeAsync(node.BaseUrl, "rdvz-audit-ok");

        var (actionId, state) = await RequestAndApproveRevokeAsync(nodeId, "310000001");

        Assert.Equal("executed", state);
        Assert.Equal("success", await ExecuteAuditResultAsync(actionId));
    }

    [Fact]
    public async Task The_execute_audit_entry_says_failed_when_the_node_refused_the_action()
    {
        await using var node = await FakeRendezvous.StartAsync(_delivered, failWith500: true);
        var nodeId = await RegisterNodeAsync(node.BaseUrl, "rdvz-audit-fail");

        var (actionId, state) = await RequestAndApproveRevokeAsync(nodeId, "310000002");

        Assert.Equal("failed", state);
        Assert.Equal("failed", await ExecuteAuditResultAsync(actionId));

        // Requesting and approving did happen, so those entries stay successful.
        var chain = await fixture.Services.GetRequiredService<OpsStore>().ReadAuditChainAsync(Ct);
        Assert.All(
            chain.Where(record => record.Target == actionId && record.Action != "action.execute"),
            record => Assert.Equal("success", record.Result));
    }

    private async Task<(string ActionId, string State)> RequestAndApproveRevokeAsync(string nodeId, string deviceId)
    {
        var (operatorClient, operatorCookies) = await SignInAsync("operator", fixture.OperatorSecret);
        string actionId;
        using (operatorClient)
        {
            var created = await PostWithCsrfAsync(operatorClient, operatorCookies, "/api/actions", new
            {
                kind = "revoke_device",
                nodeId,
                deviceId,
            });
            actionId = (await created.Content.ReadFromJsonAsync<ActionDto>(Ct))!.Id;
        }

        var (adminClient, adminCookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (adminClient)
        {
            var approved = await PostWithCsrfAsync(adminClient, adminCookies, $"/api/actions/{actionId}/approve", null);
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            return (actionId, (await approved.Content.ReadFromJsonAsync<ActionDto>(Ct))!.State);
        }
    }

    private async Task<string> ExecuteAuditResultAsync(string actionId)
    {
        var chain = await fixture.Services.GetRequiredService<OpsStore>().ReadAuditChainAsync(Ct);
        return Assert.Single(chain, record => record.Action == "action.execute" && record.Target == actionId).Result;
    }

    private async Task<string> RegisterNodeAsync(string baseUrl, string name)
    {
        var (admin, cookies) = await SignInAsync("admin", fixture.AdminSecret);
        using (admin)
        {
            var csrf = cookies.GetCookies(admin.BaseAddress!)["ops_csrf"]!.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/nodes")
            {
                Content = JsonContent.Create(new { name, role = "rendezvous", baseUrl, region = "test" }),
            };
            request.Headers.Add("X-Ops-CSRF", csrf);
            var response = await admin.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var created = await response.Content.ReadFromJsonAsync<NodeDto>(Ct);
            return created!.Id;
        }
    }

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync(string username, string secret)
    {
        var (client, cookies) = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = OpsHostFixture.Password }, Ct);
        var challenge = (await login.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.Challenge;

        var totp = await client.PostAsJsonAsync(
            "/api/auth/totp",
            new { challenge, code = fixture.NextTotp(secret) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);

        return (client, cookies);
    }

    /// <summary>A mutating request with the CSRF header the console requires of signed-in browsers.</summary>
    private static async Task<HttpResponseMessage> PostWithCsrfAsync(HttpClient client, CookieContainer cookies, string url, object? body)
    {
        var csrf = cookies.GetCookies(client.BaseAddress!)["ops_csrf"]!.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("X-Ops-CSRF", csrf);
        return await client.SendAsync(request, Ct);
    }

    /// <summary>A signaling node that records the management commands it is asked to carry out.</summary>
    private sealed class FakeRendezvous : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeRendezvous(WebApplication app, ConcurrentQueue<(string Path, string Body)> delivered, bool failWith500)
        {
            _app = app;

            app.MapPost("/internal/v1/admin/disconnect", async (HttpContext context) =>
            {
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                delivered.Enqueue(("/internal/v1/admin/disconnect", body));
                return Results.Ok(new { delivered = true });
            });

            app.MapPost("/internal/v1/admin/revoke", async (HttpContext context) =>
            {
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                delivered.Enqueue(("/internal/v1/admin/revoke", body));
                return failWith500 ? Results.StatusCode(StatusCodes.Status500InternalServerError) : Results.Ok(new { revoked = true });
            });
        }

        public string BaseUrl { get; private set; } = string.Empty;

        public static async Task<FakeRendezvous> StartAsync(ConcurrentQueue<(string, string)> delivered, bool failWith500 = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:0" });
            var app = builder.Build();
            var node = new FakeRendezvous(app, delivered, failWith500);
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            node.BaseUrl = address;
            return node;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    /// <summary>The action as the API describes it, as much of it as this class reads.</summary>
    public sealed record ActionDto(string Id, string State, string RequestedBy, string? ApprovedBy);

    /// <summary>The node as the API describes it.</summary>
    public sealed record NodeDto(string Id);

    /// <summary>The sign-in response.</summary>
    public sealed record LoginResponse(string? Status, string? Challenge);
}
