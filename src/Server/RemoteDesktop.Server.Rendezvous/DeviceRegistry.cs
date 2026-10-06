using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>Maps a device certificate fingerprint to its stable 9-digit device id.</summary>
public interface IDeviceRegistry
{
    Task<uint> GetOrAllocateAsync(string fingerprint, CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-local registry: ids survive reconnects but not a server restart. Used in tests and local development.
/// In production, <see cref="PostgresDeviceRegistry"/> persists device ids across server restarts.
/// </summary>
public sealed class InMemoryDeviceRegistry : IDeviceRegistry
{
    private readonly ConcurrentDictionary<string, uint> _byFingerprint = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, string> _byId = [];

    public Task<uint> GetOrAllocateAsync(string fingerprint, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byFingerprint.GetOrAdd(fingerprint, Allocate));

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
