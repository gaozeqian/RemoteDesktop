using System.Data.Common;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// Answers whether the console's database is reachable, without opening a connection for every request.
/// </summary>
/// <remarks>
/// <c>/health</c> is anonymous and loopback-only. Without a cache, anything that can reach the loopback socket can
/// drive one database connection and query per request; a supervisor probing every second would do the same by
/// design. Two seconds still notices a database that went away, while several probes arriving together share one
/// answer: the semaphore makes the second wait for the first rather than open a second connection.
/// </remarks>
public sealed class OpsHealthProbe(Func<CancellationToken, Task> probe, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _checkedAtTicks = time.GetUtcNow().UtcTicks - CacheDuration.Ticks;
    private int _healthy;

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        if (IsFresh())
        {
            return Healthy;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have just refreshed the answer while this one waited.
            if (IsFresh())
            {
                return Healthy;
            }

            var healthy = true;
            try
            {
                await probe(cancellationToken).ConfigureAwait(false);
            }
            catch (DbException)
            {
                // Every failure the store can produce while opening or querying is a database failure. Anything else
                // is a bug in the caller's wiring and should keep surfacing as a 500.
                healthy = false;
            }

            Volatile.Write(ref _healthy, healthy ? 1 : 0);
            Interlocked.Exchange(ref _checkedAtTicks, time.GetUtcNow().UtcTicks);
            return healthy;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool Healthy => Volatile.Read(ref _healthy) == 1;

    private bool IsFresh() =>
        time.GetUtcNow().UtcTicks - Interlocked.Read(ref _checkedAtTicks) < CacheDuration.Ticks;
}
