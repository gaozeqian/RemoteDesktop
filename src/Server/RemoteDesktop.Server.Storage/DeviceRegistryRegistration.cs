using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Server.Storage;

/// <summary>
/// The one place that knows the names of the databases this server can use. Everything else — the host included —
/// asks for <see cref="IDeviceRegistry"/> and gets whichever one the settings named.
/// </summary>
public static class DeviceRegistryRegistration
{
    /// <summary>
    /// Registers the registry the settings describe, and <see cref="IDeviceRegistryStore"/> as well when that
    /// registry keeps its ids in a database. The host reads the second registration as "start an initializer and add
    /// a health check"; there is nothing else to switch on.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings were not validated, or name a provider that is not
    /// in the switch below. <see cref="DatabaseSettings.Validate"/> is the one that is expected to catch this.</exception>
    public static IServiceCollection AddDeviceRegistry(this IServiceCollection services, DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(database);

        services.AddSingleton(database);

        switch (database.Provider)
        {
            case DatabaseSettings.None:
                services.AddSingleton<IDeviceRegistry, InMemoryDeviceRegistry>();
                return services;

            case DatabaseSettings.Postgres:
                services.AddSingleton<IDeviceRegistryStore>(sp => new PostgresDeviceRegistry(
                    database.ConnectionString!,
                    sp.GetRequiredService<ILogger<PostgresDeviceRegistry>>()));
                break;

            case DatabaseSettings.Sqlite:
                var path = System.IO.Path.GetFullPath(database.Path!);
                EnsureFolder(path);
                services.AddSingleton<IDeviceRegistryStore>(sp => new SqliteDeviceRegistry(
                    path,
                    sp.GetRequiredService<ILogger<SqliteDeviceRegistry>>()));
                break;

            default:
                throw new InvalidOperationException(
                    $"No device registry is implemented for provider '{database.Provider}'.");
        }

        // The store is the registry; asking for the port twice must not build a second one with its own cache.
        services.AddSingleton(sp => (IDeviceRegistry)sp.GetRequiredService<IDeviceRegistryStore>());
        return services;
    }

    /// <summary>
    /// The folder the database file lives in, created if it is not there.
    /// </summary>
    /// <remarks>
    /// Done here rather than in the registry's constructor so that a folder that cannot be created stops the
    /// installation with a sentence naming the folder, instead of surfacing later as a failed connection.
    /// </remarks>
    private static void EnsureFolder(string databasePath)
    {
        var folder = System.IO.Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"The folder for the SQLite database ('{folder}') could not be created: {ex.Message}. " +
                "Rendezvous:Database:Path has to name a file this service can write to.", ex);
        }
    }
}
