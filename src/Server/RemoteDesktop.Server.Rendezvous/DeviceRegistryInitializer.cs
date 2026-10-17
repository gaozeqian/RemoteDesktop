using Microsoft.Extensions.Hosting;
using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Creates the device registry's table before the server takes its first connection.
/// </summary>
/// <remarks>
/// Registered only when a persistent registry is; the in-memory one has no schema, and asking it for one would mean
/// this class had to know which implementations are databases. A failure here stops the server, which is the point:
/// a rendezvous that cannot write device ids would hand out codes it cannot keep.
/// </remarks>
public sealed class DeviceRegistryInitializer(IDeviceRegistryStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.EnsureSchemaAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
