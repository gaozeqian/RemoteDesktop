using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Whether the device registry can still be reached. The database holds the mapping from a certificate fingerprint
/// to the device code, so a rendezvous that has lost it cannot keep the promise it made to the machines that depend
/// on their code staying the same.
/// </summary>
public sealed class PostgresHealthCheck(IOptions<RendezvousOptions> options) : IHealthCheck
{
    private readonly string _connectionString =
        options.Value.PostgresConnectionString
        ?? throw new InvalidOperationException("The Postgres health check was registered without a connection string.");

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1;", connection);
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("The device registry's database did not answer.", ex);
        }
    }
}
