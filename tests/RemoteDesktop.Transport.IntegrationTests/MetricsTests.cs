using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using RemoteDesktop.Server.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// What <c>/metrics</c> answers. The numbers say how many devices this server serves and how hard it refuses the
/// rest, which on a public server is the operator's business, so the path exists only when one asked for it.
/// </summary>
public class MetricsTests
{
    private static readonly (string Key, string Value) MetricsOn = ("Rendezvous:MetricsEnabled", "true");

    /// <summary>Every metric and the type a scraper is told it has. Off means the whole path is a 404; on, these are all there.</summary>
    private static readonly Dictionary<string, string> MetricTypes = new(StringComparer.Ordinal)
    {
        ["remotedesktop_connections_open"] = "gauge",
        ["remotedesktop_connections_refused_total"] = "counter",
        ["remotedesktop_registrations_total"] = "counter",
        ["remotedesktop_registrations_refused_total"] = "counter",
        ["remotedesktop_connect_requests_accepted_total"] = "counter",
        ["remotedesktop_connect_requests_throttled_total"] = "counter",
        ["remotedesktop_connect_requests_refused_total"] = "counter",
        ["remotedesktop_devices_online"] = "gauge",
        ["remotedesktop_presence_queries_throttled_total"] = "counter",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_path_is_not_served_unless_it_is_turned_on()
    {
        await using var server = await TestRendezvous.StartAsync();
        using var client = new HttpClient();

        var answer = await client.GetAsync(MetricsUri(server), Ct);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);

        // The same server answers its health check: the 404 is the metrics path not existing, not the server not
        // being there.
        Assert.Equal("Healthy", await client.GetStringAsync(new Uri(new Uri(server.Urls.First()), "/health"), Ct));
    }

    [Fact]
    public async Task A_device_coming_online_moves_the_numbers()
    {
        await using var server = await TestRendezvous.StartAsync(MetricsOn);
        using var client = new HttpClient();

        var before = await client.GetStringAsync(MetricsUri(server), Ct);
        Assert.Equal(0, Value(before, "remotedesktop_devices_online"));
        Assert.Equal(0, Value(before, "remotedesktop_connections_open"));
        Assert.Equal(0, Value(before, "remotedesktop_registrations_total"));

        await using var device = await TestDevice.StartAsync(TestRendezvous.SignalingUri(server));

        // The server writes the device into its presence table a hair after it has answered the registration, so a
        // scrape started from this side can race the last of the registration's round trip.
        Assert.Equal(1, await EventuallyAsync(client, MetricsUri(server), "remotedesktop_devices_online", 1));

        var after = await client.GetStringAsync(MetricsUri(server), Ct);
        Assert.True(Value(after, "remotedesktop_connections_open") >= 1);
        Assert.True(Value(after, "remotedesktop_registrations_total") >= 1);
    }

    [Fact]
    public async Task A_connection_refused_over_the_quota_is_counted()
    {
        await using var server = await TestRendezvous.StartAsync(
            MetricsOn,
            ("Rendezvous:MaxConnections", "2"),
            ("Rendezvous:MaxConnectionsPerAddress", "2"));
        var uri = TestRendezvous.SignalingUri(server);

        using var first = await RawSignaling.ConnectAsync(uri);
        using var second = await RawSignaling.ConnectAsync(uri);
        await Assert.ThrowsAnyAsync<WebSocketException>(() => RawSignaling.ConnectAsync(uri));

        using var client = new HttpClient();
        var body = await client.GetStringAsync(MetricsUri(server), Ct);

        Assert.Equal(1, Value(body, "remotedesktop_connections_refused_total"));
        Assert.Equal(2, Value(body, "remotedesktop_connections_open"));

        // The two that are open never got past the challenge, so they are connections and not devices.
        Assert.Equal(0, Value(body, "remotedesktop_devices_online"));
    }

    [Fact]
    public async Task Every_metric_carries_the_name_help_and_type_a_scraper_expects()
    {
        await using var server = await TestRendezvous.StartAsync(MetricsOn);
        using var client = new HttpClient();

        var answer = await client.GetAsync(MetricsUri(server), Ct);
        var body = await answer.Content.ReadAsStringAsync(Ct);
        var contentType = answer.Content.Headers.ContentType?.ToString();

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.NotNull(contentType);
        Assert.StartsWith("text/plain", contentType, StringComparison.Ordinal);
        Assert.Contains("version=0.0.4", contentType, StringComparison.Ordinal);

        foreach (var (name, type) in MetricTypes)
        {
            Assert.StartsWith("remotedesktop_", name, StringComparison.Ordinal);
            Assert.Contains($"# HELP {name} ", body, StringComparison.Ordinal);
            Assert.Contains($"# TYPE {name} {type}", body, StringComparison.Ordinal);

            // A metric that is declared but never written is a blind spot for the scraper: Value() fails the case
            // when the sample line is missing or is not a number.
            Assert.InRange(Value(body, name), 0, long.MaxValue);
        }

        // The format needs the last line to end with a newline as well.
        Assert.EndsWith("\n", body);
    }

    private static Uri MetricsUri(WebApplication server) => new(new Uri(server.Urls.First()), RendezvousServer.MetricsPath);

    /// <summary>Reads one sample out of the exposition text, which is all a scraper is.</summary>
    private static long Value(string body, string name)
    {
        var prefix = name + " ";
        var sample = Assert.Single(body.Split('\n'), line => line.StartsWith(prefix, StringComparison.Ordinal));
        return long.Parse(sample[prefix.Length..], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Waits for a metric to reach <paramref name="expected"/>, for the numbers the server publishes a moment after
    /// the client has been told it is online.
    /// </summary>
    private static async Task<long> EventuallyAsync(HttpClient client, Uri metrics, string name, long expected)
    {
        var value = -1L;
        for (var attempt = 0; attempt < 100 && value != expected; attempt++)
        {
            value = Value(await client.GetStringAsync(metrics, Ct), name);
            if (value != expected)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
            }
        }

        // Failing here rather than handing the last sample back: by the time the caller asserts, the five seconds of
        // waiting — and the metric's name — are no longer part of the message.
        if (value != expected)
        {
            Assert.Fail($"{name} did not settle within five seconds: expected {expected}, saw {value}.");
        }

        return value;
    }
}
