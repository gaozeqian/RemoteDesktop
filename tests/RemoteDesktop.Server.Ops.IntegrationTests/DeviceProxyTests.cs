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
/// The device page's path: a real rendezvous-shaped node serves a page, the console proxies it. The console never
/// touches the device registry, and this test is where that stays true.
/// </summary>
[Collection("ops-console")]
public class DeviceProxyTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<string> _created = [];

    /// <summary>Nothing to set up per test.</summary>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Removes the nodes this test registered.</summary>
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        var store = fixture.Services.GetRequiredService<OpsStore>();
        foreach (var id in _created)
        {
            await store.DeleteNodeAsync(id, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Devices_are_proxied_from_the_selected_node()
    {
        await using var node = await FakeRendezvous.StartAsync();
        var (client, cookies) = await SignInAsync();
        using (client)
        {
            var registered = await RegisterAsync(client, cookies, node);
            var overview = await registered.Content.ReadFromJsonAsync<NodeResponse>(Ct);
            _created.Add(overview!.Id);

            var response = await client.GetFromJsonAsync<DevicePage>($"/api/devices?node={overview!.Id}", Ct);

            Assert.NotNull(response);
            Assert.Equal(node.Name, response.Node.Name);
            Assert.Equal(2, response.Items.Count);
            Assert.True(response.Items[0].Online);
            Assert.False(response.Items[1].Online);
            Assert.Equal(582441907u, response.Items[0].DeviceId);
            Assert.Null(response.NextCursor);
        }
    }

    [Fact]
    public async Task An_unknown_node_is_a_404_rather_than_an_empty_page()
    {
        var (client, _) = await SignInAsync();
        using (client)
        {
            var response = await client.GetAsync("/api/devices?node=no-such-node", Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, CookieContainer cookies, FakeRendezvous node)
    {
        var csrf = cookies.GetCookies(client.BaseAddress!)["ops_csrf"]!.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/nodes")
        {
            Content = JsonContent.Create(new { name = node.Name, role = "rendezvous", baseUrl = node.BaseUrl }),
        };
        request.Headers.Add("X-Ops-CSRF", csrf);
        return await client.SendAsync(request, Ct);
    }

    /// <summary>A node that answers the device endpoint the way the signaling server does.</summary>
    private sealed class FakeRendezvous : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeRendezvous(WebApplication app) => _app = app;

        public string Name { get; } = "fake-rdvz-" + Guid.NewGuid().ToString("N")[..8];

        public string BaseUrl { get; private set; } = string.Empty;

        public static async Task<FakeRendezvous> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:0" });
            var app = builder.Build();
            var node = new FakeRendezvous(app);

            app.MapGet("/internal/v1/devices", () => Results.Ok(new NodeDevicePage(
                [
                    new NodeDeviceSummary(582441907, 1_700_000_000_000, 1_700_000_100_000, Online: true, Revoked: false),
                    new NodeDeviceSummary(771039552, 1_700_000_200_000, 1_700_000_300_000, Online: false, Revoked: true),
                ],
                NextCursor: null)));

            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            node.BaseUrl = address;
            return node;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    /// <summary>The node registration response.</summary>
    public sealed record NodeResponse(string Id, string Name);

    /// <summary>What the console returns for a page of devices.</summary>
    public sealed record DevicePage(NodeResponse Node, List<DeviceDto> Items, string? NextCursor);

    /// <summary>One device row.</summary>
    public sealed record DeviceDto(uint DeviceId, long FirstSeenUnixMilliseconds, long LastSeenUnixMilliseconds, bool Online);

    /// <summary>The sign-in response.</summary>
    public sealed record LoginResponse(string? Status, string? Challenge);
}
