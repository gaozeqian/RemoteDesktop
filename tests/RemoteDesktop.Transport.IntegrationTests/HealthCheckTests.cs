using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// What <c>/health</c> answers. A rendezvous with a database it cannot reach is not healthy — it cannot keep the
/// device codes it has already handed out — and monitoring that is told otherwise will not come looking.
/// </summary>
public class HealthCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_server_without_a_database_reports_healthy()
    {
        await using var server = await TestRendezvous.StartAsync();
        using var client = new HttpClient();

        var answer = await client.GetStringAsync(new Uri(new Uri(server.Urls.First()), "/health"), Ct);

        Assert.Equal("Healthy", answer);
    }

    [Fact]
    public async Task A_registry_that_cannot_be_reached_reports_unhealthy()
    {
        // Port 1 on loopback: nothing listens there, so the attempt fails at once instead of waiting for a timeout.
        var check = new PostgresHealthCheck(Options.Create(new RendezvousOptions
        {
            PostgresConnectionString = "Host=127.0.0.1;Port=1;Database=remotedesktop;Username=remotedesktop;Password=x;Timeout=2",
        }));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    /// <summary>
    /// The check reads the connection string the same way the registry does, so a deployment that configures the
    /// database gets a real answer rather than a green light from a check that was never registered.
    /// </summary>
    [Fact]
    public async Task A_server_with_a_database_that_does_not_answer_is_not_green()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => TestRendezvous.StartAsync(
            ("Rendezvous:PostgresConnectionString", "Host=127.0.0.1;Port=1;Database=remotedesktop;Username=remotedesktop;Password=x;Timeout=2")));
    }
}
