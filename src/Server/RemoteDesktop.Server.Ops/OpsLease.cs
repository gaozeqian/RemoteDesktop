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

    /// <summary>
    /// Gives <paramref name="name"/> up if this process holds it, so a restarted instance does not wait out the lease.
    /// </summary>
    public async Task ReleaseAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await store.ReleaseLeaseAsync(name, _holder, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The lease length for a task that polls every <paramref name="pollInterval"/>: three intervals, at least thirty seconds.
    /// </summary>
    /// <remarks>
    /// Long enough that one slow or missed pass does not hand the work to another instance, short enough that a process
    /// that died without releasing the lease is replaced within a few polls rather than minutes.
    /// </remarks>
    public static TimeSpan DurationFor(TimeSpan pollInterval)
    {
        var minimum = TimeSpan.FromSeconds(30);
        var threeIntervals = pollInterval * 3;
        return threeIntervals > minimum ? threeIntervals : minimum;
    }
}
