using System.Net;
using System.Text.Json;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The sidebar's version comes from the assembly, not from a string in <c>index.html</c> — a string there was wrong for
/// five releases without anything failing.
/// </summary>
[Collection("ops-console")]
public class VersionEndpointTests(OpsHostFixture fixture) : IClassFixture<OpsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_version_endpoint_answers_without_a_session_and_reports_the_product_version()
    {
        var (http, _) = fixture.CreateClient();
        using var client = http;

        var response = await client.GetAsync("/api/version", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var version = json.RootElement.GetProperty("version").GetString();

        Assert.Equal(ProductVersion.Display, version);
        // Three parts, because that is what the packages and the bundle carry; the commit suffix belongs to telemetry.
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }
}
