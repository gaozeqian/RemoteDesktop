using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// How many devices the server serves at once, and how many of those one source address may hold. The count is taken
/// before the WebSocket upgrade, so a connection that is over the quota costs the sender a round trip and the server
/// no memory at all.
/// </summary>
/// <remarks>
/// The table only holds addresses that have a connection open right now, so its size is bounded by the global quota
/// however many addresses a flood comes from — it is the global ceiling that keeps this small, which is why the two
/// limits belong together.
/// </remarks>
public sealed class ConnectionQuota
{
    private readonly ConcurrentDictionary<IPAddress, int> _perAddress = [];
    private readonly RendezvousOptions _options;
    private int _open;

    public ConnectionQuota(IOptions<RendezvousOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>Connections being served right now, for logs and tests.</summary>
    public int Open => Volatile.Read(ref _open);

    /// <param name="address">The caller, when the connection has one; a request without an address is only counted globally.</param>
    /// <returns><c>false</c> when the caller is over a quota and the connection must be refused.</returns>
    public bool TryOpen(IPAddress? address)
    {
        if (Interlocked.Increment(ref _open) > _options.MaxConnections)
        {
            Interlocked.Decrement(ref _open);
            return false;
        }

        if (address is null)
        {
            return true;
        }

        if (_perAddress.AddOrUpdate(address, 1, (_, open) => open + 1) > _options.MaxConnectionsPerAddress)
        {
            Release(address);
            Interlocked.Decrement(ref _open);
            return false;
        }

        return true;
    }

    /// <summary>Gives back a slot taken by <see cref="TryOpen"/>. Every successful open is closed exactly once.</summary>
    public void Close(IPAddress? address)
    {
        Interlocked.Decrement(ref _open);
        if (address is not null)
        {
            Release(address);
        }
    }

    private void Release(IPAddress address)
    {
        while (_perAddress.TryGetValue(address, out var open))
        {
            if (open <= 1)
            {
                if (_perAddress.TryRemove(new KeyValuePair<IPAddress, int>(address, open)))
                {
                    return;
                }
            }
            else if (_perAddress.TryUpdate(address, open - 1, open))
            {
                return;
            }
        }
    }
}
