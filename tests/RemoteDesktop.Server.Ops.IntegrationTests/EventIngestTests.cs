using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The event path: sign a batch the way a server would, post it, and read the session it produced. The replay test is
/// the one that matters most — a retried batch must not double a session's byte count.
/// </summary>
[Collection("ops-console")]
public class EventIngestTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_batch_becomes_a_session_with_a_timeline()
    {
        var correlation = Guid.NewGuid();
        var batch = Lifecycle(correlation);

        var accepted = await SendAsync(batch);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var result = await accepted.Content.ReadFromJsonAsync<IngestResponse>(Ct);
        Assert.Equal(4, result!.Inserted);

        var (client, _) = await SignInAsync();
        using (client)
        {
            var sessions = await client.GetFromJsonAsync<SessionList>("/api/sessions?limit=50", Ct);
            var session = Assert.Single(sessions!.Items, item => item.CorrelationId == correlation.ToString("D"));

            Assert.Equal("relay", session.Transport);
            Assert.Equal("relay", session.Source);
            Assert.Equal(1000, session.BytesIn);
            Assert.Equal(2500, session.BytesOut);
            Assert.False(session.Active);
            Assert.Equal("idle", session.EndReason);

            var detail = await client.GetFromJsonAsync<SessionDetail>($"/api/sessions/{session.Id}", Ct);
            Assert.Equal(4, detail!.Events.Count);
            Assert.Equal(
                ["session.requested", "session.accepted", "session.relay_paired", "session.relay_closed"],
                detail.Events.Select(item => item.Kind));
        }
    }

    [Fact]
    public async Task Replaying_the_same_batch_inserts_nothing_and_keeps_the_bytes()
    {
        var correlation = Guid.NewGuid();
        var batch = Lifecycle(correlation);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(batch)).StatusCode);
        var replay = await SendAsync(batch);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var result = await replay.Content.ReadFromJsonAsync<IngestResponse>(Ct);
        Assert.Equal(0, result!.Inserted);

        var (client, _) = await SignInAsync();
        using (client)
        {
            var sessions = await client.GetFromJsonAsync<SessionList>("/api/sessions?limit=50", Ct);
            var session = Assert.Single(sessions!.Items, item => item.CorrelationId == correlation.ToString("D"));
            Assert.Equal(1000, session.BytesIn);
        }
    }

    [Fact]
    public async Task A_body_that_does_not_match_its_signature_is_refused()
    {
        var batch = Lifecycle(Guid.NewGuid());
        var body = JsonSerializer.SerializeToUtf8Bytes(batch, OpsEventJson.Options);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest/events")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add(OpsEventSigner.KeyIdHeader, "k1");
        request.Headers.Add(OpsEventSigner.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Signed over different bytes on purpose.
        request.Headers.Add(OpsEventSigner.SignatureHeader, OpsEventSigner.Sign(fixture.IngestKey, timestamp, Encoding.UTF8.GetBytes("something else")));

        using var client = new HttpClient { BaseAddress = fixture.BaseAddress };
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_stale_timestamp_is_refused_even_with_a_valid_signature()
    {
        var batch = Lifecycle(Guid.NewGuid());
        var body = JsonSerializer.SerializeToUtf8Bytes(batch, OpsEventJson.Options);
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest/events")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add(OpsEventSigner.KeyIdHeader, "k1");
        request.Headers.Add(OpsEventSigner.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(OpsEventSigner.SignatureHeader, OpsEventSigner.Sign(fixture.IngestKey, timestamp, body));

        using var client = new HttpClient { BaseAddress = fixture.BaseAddress };
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static List<OpsEventEnvelope> Lifecycle(Guid correlation)
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-30);
        string Correlation() => correlation.ToString("D");

        return
        [
            Event(OpsEventKinds.SessionRequested, start, new { controllerDeviceId = 111u, hostDeviceId = 222u, relaySessionId = Correlation() }, correlation),
            Event(OpsEventKinds.SessionAccepted, start.AddSeconds(1), new { hostDeviceId = 222u }, correlation),
            Event(OpsEventKinds.RelayPaired, start.AddSeconds(2), new { relaySessionId = Correlation(), pairedAt = start.AddSeconds(2) }, correlation),
            Event(OpsEventKinds.RelayClosed, start.AddSeconds(20), new { relaySessionId = Correlation(), bytesToController = 2500L, bytesToHost = 1000L, reason = "idle" }, correlation),
        ];
    }

    private static OpsEventEnvelope Event(string kind, DateTimeOffset at, object payload, Guid correlation) => new()
    {
        V = 1,
        EventId = Guid.CreateVersion7().ToString("D"),
        Node = kind.StartsWith("session.relay", StringComparison.Ordinal) ? "relay-test" : "rdvz-test",
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

    /// <summary>One session row.</summary>
    public sealed record SessionDto(
        string Id,
        string CorrelationId,
        string? Transport,
        string Source,
        long? BytesIn,
        long? BytesOut,
        bool Active,
        string? EndReason);

    /// <summary>One session and its events.</summary>
    public sealed record SessionDetail(SessionDto Session, List<EventDto> Events);

    /// <summary>One event in the timeline.</summary>
    public sealed record EventDto(string EventId, string Kind, string Node, long OccurredAt);
}
