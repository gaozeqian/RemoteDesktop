using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Gateway;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// Verifies the Gateway reverse proxy host: intercepts /health with 404 (loopback health is not public),
/// and forwards incoming traffic to the configured upstream cluster.
/// </summary>
public sealed class GatewayTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplication? _upstream;
    private WebApplication? _gateway;

    [Fact]
    public async Task The_public_health_endpoint_is_deliberately_not_found()
    {
        var (client, _) = await StartGatewayAsync();

        var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Forwarded_requests_reach_the_upstream_cluster()
    {
        var (client, _) = await StartGatewayAsync();

        var response = await client.GetAsync("/api/test", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal("upstream-ok", body);
    }

    private async Task<(HttpClient Client, Uri UpstreamUri)> StartGatewayAsync()
    {
        // 1. Upstream stub
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        _upstream = upstreamBuilder.Build();
        _upstream.MapGet("/api/test", () => Results.Text("upstream-ok"));
        await _upstream.StartAsync(Ct);

        var upstreamAddress = new Uri(_upstream.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single());

        // 2. Gateway instance configured to reverse proxy to our upstream
        string[] args =
        [
            "--Gateway:Listen", "http://127.0.0.1:0",
            "--ReverseProxy:Clusters:ops:Destinations:loopback:Address", upstreamAddress.ToString(),
            "--ReverseProxy:Routes:ops:ClusterId", "ops",
            "--ReverseProxy:Routes:ops:Match:Path", "/{**catchall}",
        ];

        _gateway = GatewayServer.Build(args);
        await _gateway.StartAsync(Ct);

        var gatewayAddress = new Uri(_gateway.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single());

        return (new HttpClient { BaseAddress = gatewayAddress }, upstreamAddress);
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.StopAsync(CancellationToken.None);
            await _gateway.DisposeAsync();
        }

        if (_upstream is not null)
        {
            await _upstream.StopAsync(CancellationToken.None);
            await _upstream.DisposeAsync();
        }
    }
}
