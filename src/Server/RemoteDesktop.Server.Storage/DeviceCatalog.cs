namespace RemoteDesktop.Server.Storage;

/// <summary>One registered device, with the facts the console's device page shows.</summary>
/// <param name="DeviceId">The nine-digit id handed to the device.</param>
/// <param name="FirstSeenUnixMilliseconds">When the id was allocated.</param>
/// <param name="LastSeenUnixMilliseconds">The last time that device registered, to within about a minute: a store writes it at most once a minute per device.</param>
/// <param name="Revoked">Whether an operator revoked it; the server refuses its next registration.</param>
public readonly record struct DeviceRecord(uint DeviceId, long FirstSeenUnixMilliseconds, long LastSeenUnixMilliseconds, bool Revoked);

/// <summary>
/// Reads devices out of a registry, by device id, in pages. Separate from <see cref="IDeviceRegistry"/> so that the
/// signaling path keeps its single method and does not grow a query surface it never uses.
/// </summary>
/// <remarks>
/// Cursor by device id rather than offset: ids are nine digits and roughly random, so an OFFSET would make the database
/// walk every row before the page it returns, and the walk gets longer the further an operator scrolls.
/// </remarks>
public interface IDeviceCatalog
{
    /// <summary>Up to <paramref name="limit"/> devices whose id is greater than <paramref name="afterDeviceId"/>, ordered by id.</summary>
    Task<IReadOnlyList<DeviceRecord>> ListAsync(uint afterDeviceId, int limit, CancellationToken cancellationToken = default);
}
