namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// A token bucket per signaling connection, so one client cannot use presence queries to watch a large list of codes.
/// The bucket hangs off the device's session, which the server already proved by certificate, so an anonymous caller
/// cannot spend someone else's budget. It is the connection, not the device id, that is counted: a device that
/// reconnects starts with a full bucket.
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

    private readonly KeyedTokenBucket<DeviceSession> _buckets = new(Burst, RefillInterval, time);

    public bool TryAcquire(DeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _buckets.TryTake(session);
    }

    /// <summary>
    /// Drops a device's bucket when it disconnects. The bucket is keyed by the session rather than by the device id,
    /// so a device that reconnects has already been given a fresh one and this cleanup cannot wipe that new budget.
    /// </summary>
    public void Forget(DeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _buckets.Forget(session);
    }
}
