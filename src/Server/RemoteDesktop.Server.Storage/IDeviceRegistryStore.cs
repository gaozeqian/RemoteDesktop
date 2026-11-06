namespace RemoteDesktop.Server.Storage;

/// <summary>
/// A registry that keeps its own storage: it can create the table it needs and say whether that storage is still
/// reachable.
/// </summary>
/// <remarks>
/// This is what the initializer and the health check depend on instead of the concrete type. A registry that does
/// not implement it has no schema to create and nothing to probe. The host decides whether to start them from the
/// configured provider (<c>Provider != DatabaseSettings.None</c>), which is exactly when this is registered.
/// </remarks>
public interface IDeviceRegistryStore : IDeviceRegistry
{
    /// <summary>Creates the table and its index if they are not there. Called once at start-up; failure stops the server.</summary>
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws when the storage cannot answer. The health check turns that into an unhealthy report; nothing else calls it.
    /// </summary>
    Task ProbeAsync(CancellationToken cancellationToken = default);
}
