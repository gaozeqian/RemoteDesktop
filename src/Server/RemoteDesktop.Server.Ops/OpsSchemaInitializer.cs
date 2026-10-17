using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Creates the console's tables before the first request. Deliberately a start-up step rather than a lazy check: a
/// console that cannot create its schema has nothing to serve, and failing here names the database problem at the
/// moment an operator can still read the console output.
/// </summary>
public sealed class OpsSchemaInitializer(OpsStore store) : IHostedService
{
    /// <summary>Runs <see cref="OpsStore.EnsureSchemaAsync"/>.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => store.EnsureSchemaAsync(cancellationToken);

    /// <summary>Nothing to stop.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
