using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The heartbeat's path: signed like any node event, correlated onto the relay's row when there is one, and creating a
/// row of its own when there is not. The second case is the whole point of the feature — a direct session is invisible
/// to every server except the two endpoints.
/// </summary>
[Collection("ops-console")]
public class TelemetryIngestTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_heartbeat_lands_on_the_relay_row_instead_of_adding_one()
    {
        var correlation = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddSeconds(-20);

        var accepted = await SendAsync(
        [
            Event(OpsEventKinds.SessionRequested, start, new { controllerDeviceId = 111u, hostDeviceId = 222u }, correlation),
            Event(OpsEventKinds.RelayPaired, start.AddSeconds(2), new { relaySessionId = correlation.ToString("D") }, correlation),
            // The client reports the relay's own id, in the plain form the proto carries: same row, not a second one.
            Event(
                OpsEventKinds.SessionTelemetry,
                start.AddSeconds(6),
                new
                {
                    sessionId = Guid.NewGuid().ToString("N"),
                    relaySessionId = correlation.ToString("N"),
                    role = "controller",
                    mode = "control",
                    transport = "relayed",
                    rttMilliseconds = 31,
                    version = "1.2.3",
                },
                correlation),
        ]);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var result = await accepted.Content.ReadFromJsonAsync<IngestResponse>(Ct);
        Assert.Equal(3, result!.Inserted);

        var (client, _) = await SignInAsync();
        using (client)
        {
            var sessions = await client.GetFromJsonAsync<SessionList>("/api/sessions?limit=50", Ct);
            var session = Assert.Single(sessions!.Items, item => item.CorrelationId == correlation.ToString("D"));

            Assert.Equal("relay", session.Transport);
            Assert.Equal("relay", session.Source);
            Assert.Equal(31, session.RttMs);
            Assert.Equal("1.2.3", session.Version);
        }
    }

    [Fact]
    public async Task A_direct_session_exists_only_because_a_heartbeat_said_so()
    {
        var sessionId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);

        await SendAsync(
        [
            Event(
                OpsEventKinds.SessionTelemetry,
                start,
                new
                {
                    sessionId = sessionId.ToString("N"),
                    role = "controller",
                    mode = "view",
                    transport = "direct",
                    rttMilliseconds = 12,
                    version = "1.2.3",
                },
                sessionId),
        ]);

        var (client, _) = await SignInAsync();
        using (client)
        {
            var sessions = await client.GetFromJsonAsync<SessionList>("/api/sessions?limit=50", Ct);
            var session = Assert.Single(sessions!.Items, item => item.CorrelationId == sessionId.ToString("D"));

            Assert.Equal("client", session.Source);
            Assert.Equal("direct", session.Transport);
            Assert.Equal("view", session.Mode);
            Assert.Equal(12, session.RttMs);
            Assert.True(session.Active);
        }
    }

    [Fact]
    public async Task The_overview_counts_what_the_heartbeats_reported()
    {
        var sessionId = Guid.NewGuid();
        await SendAsync(
        [
            Event(
                OpsEventKinds.SessionTelemetry,
                DateTimeOffset.UtcNow,
                new
                {
                    sessionId = sessionId.ToString("N"),
                    role = "host",
                    mode = "control",
                    transport = "direct",
                    rttMilliseconds = 0,
                    platform = "linux",
                    version = "1.2.3",
                },
                sessionId),
        ]);

        var (client, _) = await SignInAsync();
        using (client)
        {
            var overview = await client.GetFromJsonAsync<OverviewDto>("/api/overview", Ct);

            Assert.NotNull(overview);
            Assert.True(overview.DirectSessions >= 1);
            Assert.Contains(overview.Platforms, entry => entry.Platform == "linux" && entry.Sessions >= 1);
        }
    }

    private static OpsEventEnvelope Event(string kind, DateTimeOffset at, object payload, Guid correlation) => new()
    {
        V = 1,
        EventId = Guid.CreateVersion7().ToString("D"),
        Node = "rdvz-test",
        Kind = kind,
        OccurredAt = at,
        CorrelationId = correlation.ToString("D"),
        Payload = JsonSerializer.Serialize(payload, OpsEventJson.Options),
    };

    private async Task<HttpResponseMessage> SendAsync(List<OpsEventEnvelope> batch)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(batch, OpsEventJson.Options);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest/events")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add(OpsEventSigner.KeyIdHeader, "k1");
        request.Headers.Add(OpsEventSigner.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(OpsEventSigner.SignatureHeader, OpsEventSigner.Sign(fixture.IngestKey, timestamp, body));

        using var client = new HttpClient { BaseAddress = fixture.BaseAddress };
        return await client.SendAsync(request, Ct);
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

    /// <summary>What the ingest endpoint answers.</summary>
    public sealed record IngestResponse(int Accepted, int Inserted);

    /// <summary>The sign-in response, as much of it as this class reads.</summary>
    public sealed record LoginResponse(string? Status, string? Challenge);

    /// <summary>The session list.</summary>
    public sealed record SessionList(List<SessionDto> Items);

    /// <summary>One session row, with what a heartbeat can add.</summary>
    public sealed record SessionDto(
        string Id,
        string CorrelationId,
        string? Transport,
        string Source,
        string? Mode,
        double? RttMs,
        string? Platform,
        string? Version,
        bool Active);

    /// <summary>The overview, as much of it as this class reads.</summary>
    public sealed record OverviewDto(
        List<PlatformDto> Platforms,
        int DirectSessions,
        int RelayedSessions);

    /// <summary>One platform's share of the window.</summary>
    public sealed record PlatformDto(string Platform, int Sessions);
}
