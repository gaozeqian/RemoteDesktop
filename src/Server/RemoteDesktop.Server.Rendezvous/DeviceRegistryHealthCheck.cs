using Microsoft.Extensions.Diagnostics.HealthChecks;
using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Whether the device registry can still be reached. The database holds the mapping from a certificate fingerprint
/// to the device code, so a rendezvous that has lost it cannot keep the promise it made to the machines that depend
/// on their code staying the same.
/// </summary>
/// <remarks>
/// It asks the registered store rather than reading a connection string of its own. Reading one here is how the two
/// could disagree: the registry has always fallen back to <c>ConnectionStrings:Postgres</c>, and a check that only
/// knew about <c>Rendezvous:PostgresConnectionString</c> would refuse to be constructed on exactly the deployment
/// that relied on the fallback.
/// </remarks>
public sealed class DeviceRegistryHealthCheck(IDeviceRegistryStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // Everything, deliberately: a health check that throws is a health check that reports nothing, and the
            // exception is carried into the report where monitoring can see what the database said.
            return HealthCheckResult.Unhealthy("The device registry's database did not answer.", ex);
        }
    }
}
