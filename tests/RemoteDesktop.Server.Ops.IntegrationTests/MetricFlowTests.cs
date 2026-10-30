using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The M1 path end to end: register a node, scrape it, and read the numbers the page would show. The fake node is a
/// real HTTP server because the thing under test is an HTTP client plus a parser plus a query — a stubbed client would
/// skip two of the three.
/// </summary>
/// <remarks>
/// The class removes every node after each test. The overview aggregates across nodes, so a node left behind by one
/// test would make the next test's "no data" assertion pass or fail depending on execution order.
/// </remarks>
[Collection("ops-console")]
public class MetricFlowTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<string> _created = [];

    /// <summary>Nothing to set up per test.</summary>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Deletes the nodes this test registered, so the next test's "no data" assertions mean something.</summary>
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
    public async Task A_registered_node_is_scraped_and_its_gauges_reach_the_overview()
    {
        await using var node = await FakeNode.StartAsync();

        var (client, cookies) = await SignInAsync();
        using (client)
        {
            await RegisterAsync(client, cookies, node);

            var scraper = fixture.Services.GetRequiredService<MetricScraper>();
            var scraped = await scraper.RunOnceAsync(Ct);
            Assert.Equal(1, scraped);

            var overview = await client.GetFromJsonAsync<OverviewDto>("/api/overview", Ct);

            Assert.NotNull(overview);
            Assert.Equal(7, overview.Kpis.DevicesOnline);
            Assert.Equal(2, overview.Kpis.RelaySessions);
            Assert.NotNull(overview.Kpis.AlertsToday);
            var listed = Assert.Single(overview.Nodes);
            Assert.True(listed.LastScrapeOk);
            Assert.NotNull(listed.LastScrapeAt);
        }
    }

    [Fact]
    public async Task A_node_detail_lists_every_latest_metric_and_the_node_info()
    {
        await using var node = await FakeNode.StartAsync();

        var (client, cookies) = await SignInAsync();
        using (client)
        {
            await RegisterAsync(client, cookies, node);

            var scraper = fixture.Services.GetRequiredService<MetricScraper>();
            await scraper.RunOnceAsync(Ct);

            var nodes = await client.GetFromJsonAsync<List<NodeDto>>("/api/nodes", Ct);
            var detail = await client.GetFromJsonAsync<NodeDetailDto>(
                $"/api/nodes/{Uri.EscapeDataString(Assert.Single(nodes!).Id)}/detail",
                Ct);

            Assert.NotNull(detail);
            Assert.Null(detail.InfoError);
            Assert.Equal("9.9.9-test", detail.Info!.Version);
            Assert.True(detail.Info.UptimeSeconds >= 0);
            Assert.Contains(detail.Metrics, point => point.Metric == "remotedesktop_devices_online" && point.Value == 7);
            Assert.Contains(detail.Metrics, point => point.Metric == "remotedesktop_relay_bytes_to_host_total");
        }
    }

    [Fact]
    public async Task The_metric_catalog_names_what_has_been_scraped()
    {
        await using var node = await FakeNode.StartAsync();

        var (client, cookies) = await SignInAsync();
        using (client)
        {
            await RegisterAsync(client, cookies, node);
            await fixture.Services.GetRequiredService<MetricScraper>().RunOnceAsync(Ct);

            var catalog = await client.GetFromJsonAsync<List<CatalogDto>>("/api/metrics", Ct);

            Assert.NotEmpty(catalog!);
            Assert.Contains(catalog!, entry => entry.Metric == "remotedesktop_devices_online" && entry.Kind == "gauge");
        }
    }

    [Fact]
    public async Task Two_scrapes_produce_a_bandwidth_rate_and_traffic_points()
    {
        await using var node = await FakeNode.StartAsync();

        var (client, cookies) = await SignInAsync();
        using (client)
        {
            await RegisterAsync(client, cookies, node);

            var scraper = fixture.Services.GetRequiredService<MetricScraper>();

            await scraper.RunOnceAsync(Ct);
            node.Advance(64 * 1024);

            // The scraper timestamps samples from the host clock, which is virtual here; without moving it the second
            // scrape replaces the first at the same timestamp and there is no interval to compute a rate from.
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            await scraper.RunOnceAsync(Ct);

            var overview = await client.GetFromJsonAsync<OverviewDto>("/api/overview", Ct);

            Assert.NotNull(overview);
            Assert.NotNull(overview.Kpis.EgressBytesPerSecond);
            Assert.True(overview.Kpis.EgressBytesPerSecond > 0);
            Assert.NotEmpty(overview.Traffic);
            Assert.True(overview.Traffic[^1].ToHost > 0);
        }
    }

    [Fact]
    public async Task A_node_that_answers_with_an_error_is_reported_as_failed_and_produces_no_numbers()
    {
        await using var node = await FakeNode.StartAsync();
        node.Fail = true;

        var (client, cookies) = await SignInAsync();
        using (client)
        {
            await RegisterAsync(client, cookies, node);

            var scraper = fixture.Services.GetRequiredService<MetricScraper>();
            var scraped = await scraper.RunOnceAsync(Ct);
            Assert.Equal(0, scraped);

            var overview = await client.GetFromJsonAsync<OverviewDto>("/api/overview", Ct);

            Assert.NotNull(overview);
            var listed = Assert.Single(overview.Nodes);
            Assert.False(listed.LastScrapeOk);
            Assert.Null(overview.Kpis.DevicesOnline);
        }
    }

    [Fact]
    public async Task A_second_node_with_the_same_name_is_refused()
    {
        await using var node = await FakeNode.StartAsync();

        var (client, cookies) = await SignInAsync();
        using (client)
        {
            await RegisterAsync(client, cookies, node);
            var duplicate = await CreateNodeAsync(client, cookies, node);

            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }
    }

    /// <summary>
    /// The scrape has to present the node's own secret when it has one, and the deployment's when it does not. The
    /// header is asserted on the node side, because "the token is stored" is not the same claim as "the token is sent".
    /// </summary>
    [Fact]
    public async Task The_scrape_presents_the_nodes_own_token_instead_of_the_deployment_token()
    {
        await using var node = await FakeNode.StartAsync();
        var options = fixture.Services.GetRequiredService<OpsOptions>();
        var previous = options.NodeToken;
        options.NodeToken = "deployment-secret";

        var (client, cookies) = await SignInAsync();
        using (client)
        {
            try
            {
                await RegisterAsync(client, cookies, node, token: "node-secret-0123456789");

                await fixture.Services.GetRequiredService<MetricScraper>().RunOnceAsync(Ct);

                Assert.Equal("Bearer node-secret-0123456789", Assert.Single(node.SeenAuthorizations));
            }
            finally
            {
                options.NodeToken = previous;
            }
        }
    }

    /// <summary>A node without its own token keeps authenticating with the deployment one: an upgrade changes nothing.</summary>
    [Fact]
    public async Task A_node_without_its_own_token_still_uses_the_deployment_token()
    {
        await using var node = await FakeNode.StartAsync();
        var options = fixture.Services.GetRequiredService<OpsOptions>();
        var previous = options.NodeToken;
        options.NodeToken = "deployment-secret";

        var (client, cookies) = await SignInAsync();
        using (client)
        {
            try
            {
                await RegisterAsync(client, cookies, node);

                await fixture.Services.GetRequiredService<MetricScraper>().RunOnceAsync(Ct);

                Assert.Equal("Bearer deployment-secret", Assert.Single(node.SeenAuthorizations));
            }
            finally
            {
                options.NodeToken = previous;
            }
        }
    }

    private async Task<(HttpClient Client, CookieContainer Cookies)> SignInAsync()
    {
        var (client, cookies) = fixture.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "admin", password = OpsHostFixture.Password },
            Ct);
        var challenge = (await login.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.Challenge;

        var totp = await client.PostAsJsonAsync(
            "/api/auth/totp",
            new { challenge, code = fixture.NextTotp(fixture.AdminSecret) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);

        return (client, cookies);
    }

    private async Task RegisterAsync(HttpClient client, CookieContainer cookies, FakeNode node, string? token = null)
    {
        var response = await CreateNodeAsync(client, cookies, node, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedNode>(Ct);
        _created.Add(created!.Id);
    }

    private static async Task<HttpResponseMessage> CreateNodeAsync(
        HttpClient client,
        CookieContainer cookies,
        FakeNode node,
        string? token = null)
    {
        var csrf = cookies.GetCookies(client.BaseAddress!)["ops_csrf"]!.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/nodes")
        {
            Content = JsonContent.Create(new
            {
                name = node.Name,
                role = "relay",
                baseUrl = node.BaseUrl,
                region = "test",
                token,
            }),
        };
        request.Headers.Add("X-Ops-CSRF", csrf);

        return await client.SendAsync(request, Ct);
    }

    /// <summary>
    /// A minimal relay-shaped node: the metric names the console knows, with counters the test can advance.
    /// </summary>
    private sealed class FakeNode : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeNode(WebApplication app)
        {
            _app = app;
        }

        public string Name { get; } = "fake-relay-" + Guid.NewGuid().ToString("N")[..8];

        public string BaseUrl { get; private set; } = string.Empty;

        /// <summary>Whether the node answers 503, which is how a broken scrape is simulated.</summary>
        public bool Fail { get; set; }

        public string Version { get; } = "9.9.9-test";

        /// <summary>Every <c>Authorization</c> header the node saw on <c>/metrics</c>, in order.</summary>
        public List<string?> SeenAuthorizations { get; } = [];

        public long StartedAtUnixMilliseconds { get; } = DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds();

        private long Bytes { get; set; }

        public void Advance(long bytes) => Bytes += bytes;

        public static async Task<FakeNode> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:0" });
            builder.Logging.ClearProviders();

            var app = builder.Build();
            var node = new FakeNode(app);

            app.MapGet("/metrics", (HttpContext context) =>
            {
                node.SeenAuthorizations.Add(context.Request.Headers.Authorization);

                if (node.Fail)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                var text = $"""
                    # TYPE remotedesktop_devices_online gauge
                    remotedesktop_devices_online 7
                    # TYPE remotedesktop_relay_pairings_active gauge
                    remotedesktop_relay_pairings_active 2
                    # TYPE remotedesktop_relay_bytes_to_host_total counter
                    remotedesktop_relay_bytes_to_host_total {node.Bytes}
                    # TYPE remotedesktop_relay_bytes_to_controller_total counter
                    remotedesktop_relay_bytes_to_controller_total {node.Bytes * 2}
                    """;
                return Results.Text(text, "text/plain; version=0.0.4; charset=utf-8");
            });

            app.MapGet("/internal/v1/info", () => Results.Ok(new
            {
                name = node.Name,
                role = "relay",
                version = node.Version,
                startedAtUnixMilliseconds = node.StartedAtUnixMilliseconds,
            }));

            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            node.BaseUrl = address;
            return node;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    /// <summary>The overview JSON, as much of it as these tests read.</summary>
    public sealed record OverviewDto(KpisDto Kpis, List<TrafficDto> Traffic, List<NodeDto> Nodes);

    /// <summary>The KPI block. Nullable throughout: the API returns null rather than a made-up zero.</summary>
    public sealed record KpisDto(double? DevicesOnline, double? RelaySessions, double? EgressBytesPerSecond, double? AlertsToday);

    /// <summary>One traffic point.</summary>
    public sealed record TrafficDto(long Timestamp, double ToHost, double ToController);

    /// <summary>One node row.</summary>
    public sealed record NodeDto(string Id, string Name, string Role, bool Enabled, long? LastScrapeAt, bool? LastScrapeOk);

    /// <summary>The sign-in response.</summary>
    public sealed record LoginResponse(string? Status, string? Challenge);

    /// <summary>What the node endpoint returns after registering one.</summary>
    public sealed record CreatedNode(string Id);

    /// <summary>The node detail, as much of it as these tests read.</summary>
    public sealed record NodeDetailDto(InfoDto? Info, string? InfoError, List<MetricDto> Metrics);

    /// <summary>The node's self-description.</summary>
    public sealed record InfoDto(string Name, string Role, string Version, long StartedAtUnixMilliseconds, long UptimeSeconds);

    /// <summary>One latest reading.</summary>
    public sealed record MetricDto(string Metric, long Timestamp, double Value);

    /// <summary>One metric catalog row.</summary>
    public sealed record CatalogDto(string Metric, string Kind);
}
