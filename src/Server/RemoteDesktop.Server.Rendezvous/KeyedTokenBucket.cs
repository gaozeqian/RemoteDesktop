using System.Collections.Concurrent;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// A token bucket per key: how often one address may register a device, how often one device may ask for a
/// connection, how often one client may ask about presence.
/// </summary>
/// <remarks>
/// Keys arrive from the network, so the table has to be able to shrink again — a limiter that only ever grows is
/// itself the attack. Buckets nobody has touched for a while are dropped when a sweep comes due, and the sweep is
/// timed rather than counted so that a flood cannot make every call pay for it.
/// </remarks>
/// <typeparam name="TKey">
/// Compared by identity, which is what the presence limiter needs: a device that reconnects gets a fresh budget
/// instead of inheriting the old session's, and the dying session's cleanup cannot wipe the new session's budget.
/// </typeparam>
/// <param name="burst">How many tokens a key starts with.</param>
/// <param name="refill">How long one token takes to come back.</param>
/// <param name="time">The clock, so tests can move it.</param>
public sealed class KeyedTokenBucket<TKey>(int burst, TimeSpan refill, TimeProvider time)
    where TKey : notnull
{
    private static readonly TimeSpan IdleRetention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<TKey, Bucket> _buckets = [];
    private readonly Lock _sweepLock = new();
    private DateTimeOffset _nextSweep = time.GetUtcNow() + SweepInterval;

    /// <summary>Buckets being tracked, for logs and tests.</summary>
    public int Count => _buckets.Count;

    /// <summary>Takes one token for <paramref name="key"/>, or reports that its budget is spent.</summary>
    public bool TryTake(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var now = time.GetUtcNow();
        Sweep(now);
        return _buckets.GetOrAdd(key, _ => new Bucket(burst, now)).TryTake(now, burst, refill);
    }

    /// <summary>
    /// Gives a token back, for the times a token was taken for something that turned out not to happen — a device
    /// that reconnects is not a new device. Never above a full burst, so this cannot be used to bank credit.
    /// </summary>
    public void Refund(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_buckets.TryGetValue(key, out var bucket))
        {
            bucket.Refund(time.GetUtcNow(), burst);
        }
    }

    /// <summary>Drops a key's bucket, so whatever comes next for it starts with a full burst again.</summary>
    public void Forget(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _buckets.TryRemove(key, out _);
    }

    private void Sweep(DateTimeOffset now)
    {
        if (now < _nextSweep)
        {
            return;
        }

        lock (_sweepLock)
        {
            if (now < _nextSweep)
            {
                return;
            }

            _nextSweep = now + SweepInterval;
            foreach (var (key, bucket) in _buckets)
            {
                if (now.UtcTicks - Volatile.Read(ref bucket.LastUsedTicks) >= IdleRetention.Ticks)
                {
                    _buckets.TryRemove(new KeyValuePair<TKey, Bucket>(key, bucket));
                }
            }
        }
    }

    private sealed class Bucket
    {
        private readonly Lock _lock = new();
        private int _tokens;
        private DateTimeOffset _refilledAt;

        public Bucket(int burst, DateTimeOffset now)
        {
            _tokens = burst;
            _refilledAt = now;
            LastUsedTicks = now.UtcTicks;
        }

        /// <summary>When the bucket was last spent from, as ticks so that the sweep can read it without tearing.</summary>
        public long LastUsedTicks;

        public void Refund(DateTimeOffset now, int burst)
        {
            lock (_lock)
            {
                Volatile.Write(ref LastUsedTicks, now.UtcTicks);
                _tokens = Math.Min(burst, _tokens + 1);
            }
        }

        public bool TryTake(DateTimeOffset now, int burst, TimeSpan refill)
        {
            lock (_lock)
            {
                Volatile.Write(ref LastUsedTicks, now.UtcTicks);

                var elapsed = now - _refilledAt;
                if (elapsed >= refill)
                {
                    var earned = (int)(elapsed / refill);
                    _tokens = Math.Min(burst, _tokens + earned);
                    _refilledAt += earned * refill;
                }

                if (_tokens == 0)
                {
                    return false;
                }

                _tokens--;
                return true;
            }
        }
    }
}
