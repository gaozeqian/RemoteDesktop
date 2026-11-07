using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

[Collection("ops-console")]
public class ChannelManagementTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Channels_can_be_managed_via_store()
    {
        var store = fixture.Services.GetRequiredService<OpsStore>();
        var channelId = "chan-" + Guid.NewGuid().ToString("N")[..8];
        var channel = new NotificationChannel(channelId, "webhook", "https://example.com/hook", "secret123", Enabled: true);

        await store.UpsertNotificationChannelAsync(channel, Ct);

        var loaded = await store.GetNotificationChannelAsync(channelId, Ct);
        Assert.NotNull(loaded);
        Assert.Equal("webhook", loaded.Kind);
        Assert.Equal("https://example.com/hook", loaded.Target);
        Assert.Equal("secret123", loaded.Secret);
        Assert.True(loaded.Enabled);

        var list = await store.ListNotificationChannelsAsync(Ct);
        Assert.Contains(list, c => c.Id == channelId);

        var deleted = await store.DeleteNotificationChannelAsync(channelId, Ct);
        Assert.True(deleted);

        var afterDelete = await store.GetNotificationChannelAsync(channelId, Ct);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task Channels_endpoint_allows_listing_creating_and_deleting()
    {
        var (client, cookies) = await SignInAsync();
        using (client)
        {
            var csrf = cookies.GetCookies(fixture.BaseAddress)["ops_csrf"]!.Value;
            var channelId = "api-chan-" + Guid.NewGuid().ToString("N")[..8];

            // 1. Create channel via POST
            using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/settings/channels")
            {
                Content = JsonContent.Create(new
                {
                    id = channelId,
                    kind = "webhook",
                    target = "https://hooks.slack.com/test",
                    secret = "webhook-key",
                    enabled = true,
                }),
            };
            createReq.Headers.Add("X-Ops-CSRF", csrf);

            var createResp = await client.SendAsync(createReq, Ct);
            Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

            // 2. List channels via GET (secret is masked)
            var channels = await client.GetFromJsonAsync<List<ChannelDto>>("/api/settings/channels", Ct);
            Assert.NotNull(channels);
            var found = Assert.Single(channels, c => c.Id == channelId);
            Assert.Equal("webhook", found.Kind);
            Assert.Equal("https://hooks.slack.com/test", found.Target);
            Assert.True(found.HasSecret);

            // 3. Delete channel via DELETE
            using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/settings/channels/{channelId}");
            deleteReq.Headers.Add("X-Ops-CSRF", csrf);

            var deleteResp = await client.SendAsync(deleteReq, Ct);
            Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

            // 4. Verify gone
            var afterList = await client.GetFromJsonAsync<List<ChannelDto>>("/api/settings/channels", Ct);
            Assert.DoesNotContain(afterList!, c => c.Id == channelId);
        }
    }

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync()
    {
        var (client, cookies) = fixture.CreateClient();
        var step1 = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = OpsHostFixture.Password }, Ct);
        var challenge = (await step1.Content.ReadFromJsonAsync<LoginChallengeResponse>(Ct))!.Challenge;
        var step2 = await client.PostAsJsonAsync("/api/auth/totp", new { challenge, code = fixture.NextTotp(fixture.AdminSecret) }, Ct);
        Assert.Equal(HttpStatusCode.OK, step2.StatusCode);
        return (client, cookies);
    }

    private sealed record LoginChallengeResponse(string Challenge);
    private sealed record ChannelDto(string Id, string Kind, string Target, bool HasSecret, bool Enabled);
}
