using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RemoteDesktop.Server.Rendezvous;

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
}

/// <summary>
/// Process-local registry: ids survive reconnects but not a server restart. Used in tests and local development.
/// In production, <see cref="PostgresDeviceRegistry"/> persists device ids across server restarts.
/// </summary>
public sealed class InMemoryDeviceRegistry : IDeviceRegistry
{
    private readonly ConcurrentDictionary<string, uint> _byFingerprint = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, string> _byId = [];

    public Task<DeviceRegistration> GetOrAllocateAsync(string fingerprint, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetOrAllocate(fingerprint));

    private DeviceRegistration GetOrAllocate(string fingerprint)
    {
        while (true)
        {
            if (_byFingerprint.TryGetValue(fingerprint, out var known))
            {
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
                return new DeviceRegistration(id, Created: true);
            }

            // Another connection registered the same fingerprint between the two calls; its id is the one that counts.
            _byId.TryRemove(new KeyValuePair<uint, string>(id, fingerprint));
        }
    }
}
