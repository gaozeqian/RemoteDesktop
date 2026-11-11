using System.Net;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// <c>/health</c> is for a local curl or a supervisor. docs/ops/README.md promises it answers loopback callers only and
/// refuses a request that came through a reverse proxy, which also connects from loopback.
/// </summary>
[Collection("ops-console")]
public class HealthEndpointTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_loopback_caller_with_no_forwarded_header_gets_the_database_status()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"healthy\"", body, StringComparison.Ordinal);
        Assert.Contains("\"sqlite\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("Forwarded", "for=203.0.113.9")]
    public async Task A_request_that_came_through_a_proxy_is_not_answered(string header, string value)
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation(header, value);

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The same promise for the scheme header alone: with an empty <c>Ops:TrustedProxies</c> the forwarded headers
    /// are not read, so the request stays http however the caller spells the header.
    /// </summary>
    [Fact]
    public async Task A_request_carrying_only_the_scheme_header_is_not_answered()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
