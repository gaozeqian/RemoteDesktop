using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// The identity this process uses when it takes a background-task lease.
/// </summary>
/// <remarks>
/// A lease is renewed by the same holder, so the id has to be stable for the life of the process and different from
/// every other process — including a second instance on the same machine. A second holder that loses the lease stops
/// doing the work instead of sending every alert twice or running retention concurrently with the first.
/// </remarks>
public sealed class OpsLease(OpsStore store, TimeProvider time)
{
    private readonly string _holder = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    /// <summary>Takes or renews <paramref name="name"/> for <paramref name="duration"/>.</summary>
    public async Task<bool> TryAcquireAsync(string name, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var now = time.GetUtcNow();
        return await store.TryAcquireLeaseAsync(
            name,
            _holder,
            now.ToUnixTimeMilliseconds(),
            now.Add(duration).ToUnixTimeMilliseconds(),
            cancellationToken).ConfigureAwait(false);
    }
}
