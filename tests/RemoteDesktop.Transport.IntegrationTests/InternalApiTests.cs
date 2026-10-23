using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The console's southbound face. It shares the signaling port, so without the token an unauthenticated caller from
/// the network could enumerate every device code and revoke devices; the token is what stands between those two.
/// </summary>
public class InternalApiTests
{
    private const string Token = "test-node-token";

    private static readonly (string Key, string Value) TokenSetting = ("Rendezvous:NodeToken", Token);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_token_protects_info_and_the_device_list()
    {
        await using var server = await TestRendezvous.StartAsync(TokenSetting);
        using var client = new HttpClient { BaseAddress = new Uri(server.Urls.First()) };

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/internal/v1/info", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/internal/v1/devices", Ct)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/v1/info");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_token_protects_the_admin_actions()
    {
        await using var server = await TestRendezvous.StartAsync(TokenSetting);
        using var client = new HttpClient { BaseAddress = new Uri(server.Urls.First()) };

        using var unauthorized = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/admin/revoke")
        {
            Content = JsonContent.Create(new { deviceId = "123456789", revoked = true }),
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unauthorized, Ct)).StatusCode);

        using var authorized = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/admin/revoke")
        {
            Content = JsonContent.Create(new { deviceId = "123456789", revoked = true }),
        };
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(authorized, Ct)).StatusCode);
    }

    /// <summary>
    /// A deployment without a token still serves a console on this machine; only the network is refused. This is the
    /// behavior the existing metrics and node tests rely on, and the reason the guard checks the peer address instead
    /// of simply refusing every request.
    /// </summary>
    [Fact]
    public async Task Without_a_token_loopback_is_still_allowed()
    {
        await using var server = await TestRendezvous.StartAsync();
        using var client = new HttpClient();

        var answer = await client.GetAsync(new Uri(new Uri(server.Urls.First()), "/internal/v1/info"), Ct);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
    }
}
