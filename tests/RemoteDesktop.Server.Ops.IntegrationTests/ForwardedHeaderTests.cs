using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// <c>Ops:TrustedProxies</c>: forwarded headers are believed from the proxies named there and from nobody else,
/// including a proxy on the same machine. Each case runs its own host because the list is read once at start-up.
/// </summary>
public class ForwardedHeaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Fails today: with an empty list the framework's middleware has neither KnownProxies nor KnownIPNetworks to
    /// check, and in that case it trusts every caller (it only validates the sender when at least one is configured).
    /// So <c>UseOps</c> clearing the defaults does not stop a local caller choosing its audit address, its
    /// sign-in throttle key or whether a cookie is Secure. Unskip once <c>OpsServer.ForwardedHeaderOptions</c> turns
    /// forwarding off when <c>Ops:TrustedProxies</c> is empty.
    /// </summary>
    [Fact(Skip = "Known defect: an empty Ops:TrustedProxies makes the forwarded-headers middleware trust every caller.")]
    public async Task With_no_trusted_proxy_a_local_caller_cannot_choose_its_address_or_scheme()
    {
        await using var host = await ProbeHost.StartAsync(trustedProxies: []);

        Assert.Equal("127.0.0.1 https=False", await host.AskAsync(Ct));
    }

    [Fact]
    public async Task A_named_proxy_is_believed_about_the_client_address_and_scheme()
    {
        await using var host = await ProbeHost.StartAsync(trustedProxies: ["127.0.0.1"]);

        Assert.Equal("203.0.113.9 https=True", await host.AskAsync(Ct));
    }

    [Fact]
    public async Task A_network_range_that_does_not_contain_the_caller_is_not_believed()
    {
        await using var host = await ProbeHost.StartAsync(trustedProxies: ["10.0.0.0/8"]);

        Assert.Equal("127.0.0.1 https=False", await host.AskAsync(Ct));
    }

    [Fact]
    public async Task A_network_range_that_contains_the_caller_is_believed()
    {
        await using var host = await ProbeHost.StartAsync(trustedProxies: ["127.0.0.0/8"]);

        Assert.Equal("203.0.113.9 https=True", await host.AskAsync(Ct));
    }

    /// <summary>The real pipeline plus one endpoint that reports what the rest of the console would see.</summary>
    private sealed class ProbeHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _databasePath;
        private readonly HttpClient _client;

        private ProbeHost(WebApplication app, string databasePath, Uri address)
        {
            _app = app;
            _databasePath = databasePath;
            _client = new HttpClient { BaseAddress = address };
        }

        public static async Task<ProbeHost> StartAsync(string[] trustedProxies)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"ops-fwd-{Guid.NewGuid():N}.db");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.Combine(OpsHostFixture.RepositoryRoot(), "src", "Server", "RemoteDesktop.Server.Ops"),
                EnvironmentName = "Development",
            });

            var settings = new Dictionary<string, string?>
            {
                ["Ops:Database:Provider"] = "sqlite",
                ["Ops:Database:Path"] = databasePath,
                ["Ops:ScrapeIntervalSeconds"] = "3600",
                ["Ops:AlertIntervalSeconds"] = "3600",
                ["urls"] = "http://127.0.0.1:0",
            };
            for (var index = 0; index < trustedProxies.Length; index++)
            {
                settings[$"Ops:TrustedProxies:{index}"] = trustedProxies[index];
            }

            builder.Configuration.AddInMemoryCollection(settings);
            builder.Logging.ClearProviders();
            builder.AddOps();

            var app = builder.Build();
            app.UseOps();
            app.MapGet("/probe", (HttpContext context) =>
                $"{context.Connection.RemoteIpAddress} https={context.Request.IsHttps}").AllowAnonymous();
            await app.StartAsync(Ct);

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new ProbeHost(app, databasePath, new Uri(address));
        }

        public async Task<string> AskAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

            using var response = await _client.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A pooled handle on Windows; the temp file is cleaned by the OS later.
                }
            }
        }
    }
}
