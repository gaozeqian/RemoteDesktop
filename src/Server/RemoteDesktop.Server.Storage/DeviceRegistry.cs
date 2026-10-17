using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RemoteDesktop.Server.Storage;

/// <param name="DeviceId">The stable id belonging to the fingerprint.</param>
/// <param name="Created">
/// Whether this call is what gave the fingerprint its id. A device that comes back after a dropped connection is not
/// a new device: the server counts new identities against a budget and lets reconnections through, and only the
/// registry can tell the two apart.
/// </param>
public readonly record struct DeviceRegistration(uint DeviceId, bool Created);

/// <summary>Maps a device certificate fingerprint to its stable 9-digit device id.</summary>
public interface IDeviceRegistry
{
    Task<DeviceRegistration> GetOrAllocateAsync(string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Whether an operator revoked this device, which makes the server refuse its next registration.</summary>
    Task<bool> IsRevokedAsync(uint deviceId, CancellationToken cancellationToken = default);

    /// <summary>Revokes or un-revokes a device. Revoking does not reach into a live session; it stops the next registration.</summary>
    Task SetRevokedAsync(uint deviceId, bool revoked, CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-local registry: ids survive reconnects but not a server restart. Used in tests and local development, and
/// when no database is configured. <see cref="RelationalDeviceRegistry"/> persists them across restarts.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="IDeviceRegistryStore"/>: there is no schema to create and nothing to probe, and the
/// host reads that absence as "do not start an initializer, do not add a health check".
/// </remarks>
public sealed class InMemoryDeviceRegistry : IDeviceRegistry, IDeviceCatalog
{
    private readonly ConcurrentDictionary<string, uint> _byFingerprint = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, string> _byId = [];
    private readonly ConcurrentDictionary<uint, DateTimeOffset> _created = [];
    private readonly ConcurrentDictionary<uint, DateTimeOffset> _lastSeen = [];
    private readonly ConcurrentDictionary<uint, bool> _revoked = [];

    public Task<DeviceRegistration> GetOrAllocateAsync(string fingerprint, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetOrAllocate(fingerprint));

    public Task<bool> IsRevokedAsync(uint deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_revoked.TryGetValue(deviceId, out var revoked) && revoked);

    public Task SetRevokedAsync(uint deviceId, bool revoked, CancellationToken cancellationToken = default)
    {
        if (revoked)
        {
            _revoked[deviceId] = true;
        }
        else
        {
            _revoked.TryRemove(deviceId, out _);
        }

        return Task.CompletedTask;
    }

    /// <summary>Process-local devices, in id order. Timestamps start when the id was handed out; a restart forgets them.</summary>
    public Task<IReadOnlyList<DeviceRecord>> ListAsync(uint afterDeviceId, int limit, CancellationToken cancellationToken = default)
    {
        var records = _byId.Keys
            .Where(id => id > afterDeviceId)
            .OrderBy(id => id)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(id => new DeviceRecord(
                id,
                _created.TryGetValue(id, out var created) ? created.ToUnixTimeMilliseconds() : 0,
                _lastSeen.TryGetValue(id, out var seen) ? seen.ToUnixTimeMilliseconds() : 0,
                _revoked.TryGetValue(id, out var revoked) && revoked))
            .ToArray();

        return Task.FromResult<IReadOnlyList<DeviceRecord>>(records);
    }

    private DeviceRegistration GetOrAllocate(string fingerprint)
    {
        while (true)
        {
            if (_byFingerprint.TryGetValue(fingerprint, out var known))
            {
                _lastSeen[known] = DateTimeOffset.UtcNow;
                return new DeviceRegistration(known, Created: false);
            }

            // Random rather than sequential so ids cannot be enumerated.
            var id = (uint)RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
            if (!_byId.TryAdd(id, fingerprint))
            {
                continue; // an id collision, which is rare enough to be worth another draw
            }

            if (_byFingerprint.TryAdd(fingerprint, id))
            {
                var now = DateTimeOffset.UtcNow;
                _created[id] = now;
                _lastSeen[id] = now;
                return new DeviceRegistration(id, Created: true);
            }

            // Another connection registered the same fingerprint between the two calls; its id is the one that counts.
            _byId.TryRemove(new KeyValuePair<uint, string>(id, fingerprint));
        }
    }
}
