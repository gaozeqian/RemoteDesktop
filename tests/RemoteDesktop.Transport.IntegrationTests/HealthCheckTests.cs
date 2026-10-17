using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.Server.Storage;

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
        using var registry = new PostgresDeviceRegistry(
            "Host=127.0.0.1;Port=1;Database=remotedesktop;Username=remotedesktop;Password=x;Timeout=2",
            NullLogger<PostgresDeviceRegistry>.Instance);

        var result = await new DeviceRegistryHealthCheck(registry).CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    /// <summary>
    /// The check asks the registered store rather than reading a connection string of its own, so a deployment that
    /// configures the database gets a real answer rather than a green light from a check that was never registered.
    /// </summary>
    [Fact]
    public async Task A_server_with_a_database_that_does_not_answer_is_not_green()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => TestRendezvous.StartAsync(
            ("Rendezvous:PostgresConnectionString", "Host=127.0.0.1;Port=1;Database=remotedesktop;Username=remotedesktop;Password=x;Timeout=2")));
    }

    /// <summary>
    /// A SQLite deployment is a persistent one, so the same promise applies: the table is created before the first
    /// connection is accepted, and <c>/health</c> answers for it.
    /// </summary>
    [Fact]
    public async Task A_sqlite_server_starts_and_reports_healthy()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rd-health-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var server = await TestRendezvous.StartAsync(
                ("Rendezvous:Database:Provider", DatabaseSettings.Sqlite),
                ("Rendezvous:Database:Path", Path.Combine(folder, "rendezvous.db")));

            using var client = new HttpClient();
            var answer = await client.GetStringAsync(new Uri(new Uri(server.Urls.First()), "/health"), Ct);

            Assert.Equal("Healthy", answer);
            Assert.True(File.Exists(Path.Combine(folder, "rendezvous.db")));
        }
        finally
        {
            // The driver pools by connection string; the file stays open until the pool lets go of it.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
