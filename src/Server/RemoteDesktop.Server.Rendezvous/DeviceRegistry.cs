using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>Maps a device certificate fingerprint to its stable 9-digit device id.</summary>
public interface IDeviceRegistry
{
    uint GetOrAllocate(string fingerprint);
}

/// <summary>
/// Process-local registry: ids survive reconnects but not a server restart. Replaced by a PostgreSQL-backed
/// implementation when the server is deployed for real.
/// </summary>
public sealed class InMemoryDeviceRegistry : IDeviceRegistry
{
    private readonly ConcurrentDictionary<string, uint> _byFingerprint = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, string> _byId = [];

    public uint GetOrAllocate(string fingerprint) => _byFingerprint.GetOrAdd(fingerprint, Allocate);

    private uint Allocate(string fingerprint)
    {
        while (true)
        {
            // Random rather than sequential so ids cannot be enumerated.
            var id = (uint)RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);
            if (_byId.TryAdd(id, fingerprint))
            {
                return id;
            }
        }
    }
}
