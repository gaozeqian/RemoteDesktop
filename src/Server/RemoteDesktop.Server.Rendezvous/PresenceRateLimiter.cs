using System.Collections.Concurrent;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// A token bucket per registered device, so one client cannot use presence queries to watch a large list of codes.
/// The bucket hangs off the device's session, which the server already proved by certificate, so an anonymous caller
/// cannot spend someone else's budget.
/// </summary>
/// <remarks>
/// Ten queries at once, then one every six seconds — around ten a minute. The client asks about its whole list once
/// every thirty seconds, so it sits well inside this, with room for a second client on the same machine and for the
/// refresh button.
/// </remarks>
public sealed class PresenceRateLimiter(TimeProvider time)
{
    private const int Burst = 10;

    private static readonly TimeSpan RefillInterval = TimeSpan.FromSeconds(6);

    private readonly ConcurrentDictionary<uint, Bucket> _buckets = [];

    public bool TryAcquire(DeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var now = time.GetUtcNow();

        // A reconnect installs a new session, and the budget does not carry over to it: the old session's bucket is
        // replaced rather than inherited, or a client that reconnects could never ask again.
        var bucket = _buckets.AddOrUpdate(
            session.DeviceId,
            _ => new Bucket(session, Burst, now),
            (_, existing) => ReferenceEquals(existing.Session, session) ? existing : new Bucket(session, Burst, now));

        return bucket.TryTake(now);
    }

    /// <summary>
    /// Drops a device's bucket when it disconnects. The stored session is compared first: a device that reconnects
    /// installs a new session and aborts the old one, and that old one's cleanup must not wipe the new one's budget.
    /// </summary>
    public void Forget(DeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_buckets.TryGetValue(session.DeviceId, out var bucket) && ReferenceEquals(bucket.Session, session))
        {
            _buckets.TryRemove(new KeyValuePair<uint, Bucket>(session.DeviceId, bucket));
        }
    }

    private sealed class Bucket(DeviceSession session, int tokens, DateTimeOffset now)
    {
        private readonly Lock _lock = new();
        private int _tokens = tokens;
        private DateTimeOffset _refilledAt = now;

        public DeviceSession Session { get; } = session;

        public bool TryTake(DateTimeOffset now)
        {
            lock (_lock)
            {
                var elapsed = now - _refilledAt;
                if (elapsed >= RefillInterval)
                {
                    var earned = (int)(elapsed / RefillInterval);
                    _tokens = Math.Min(Burst, _tokens + earned);
                    _refilledAt += earned * RefillInterval;
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
