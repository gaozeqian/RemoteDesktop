namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Decides how many incoming connections may be in their handshake at the same time. Anyone who can reach this
/// computer's port can start one, and a handshake costs a task, buffers and a session ticket until it times out;
/// without a limit a flood of half-open connections would grow the service without bound. Connections beyond the limit
/// are dropped straight away (the flooder pays a round trip, this process pays nothing), while a connection that got
/// through is unaffected: the slot is held only for the handshake, not for the session.
/// </summary>
public sealed class ConnectionAdmission(int limit) : IDisposable
{
    public const int DefaultLimit = 32;

    private readonly SemaphoreSlim _slots = new(limit, limit);
    private long _refused;

    public ConnectionAdmission()
        : this(DefaultLimit)
    {
    }

    public int Limit { get; } = limit;

    /// <summary>Handshakes going on right now.</summary>
    public int InFlight => Limit - _slots.CurrentCount;

    /// <summary>How many connections have been turned away since start-up (a large number means somebody is trying).</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <returns><c>false</c> when the limit is reached and the caller should drop the connection.</returns>
    public bool TryEnter() => _slots.Wait(0);

    public void Release()
    {
        try
        {
            _slots.Release();
        }
        catch (SemaphoreFullException)
        {
            // Released one time too many; keeping the count straight matters more than the caller's mistake.
        }
    }

    /// <summary>Counts a refused connection for diagnostics.</summary>
    public void RecordRefusal() => Interlocked.Increment(ref _refused);

    public void Dispose() => _slots.Dispose();
}
