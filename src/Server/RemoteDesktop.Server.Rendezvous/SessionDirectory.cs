using System.Collections.Concurrent;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>Server-minted session ids and the only two devices allowed to report on each session.</summary>
/// <remarks>
/// Registration is on the connection path, so it never walks the whole table. Expiry is instead paid a little at a
/// time: each registration takes up to <see cref="SweepBatch"/> ids from a rotating queue, drops the ones neither
/// participant has reported within <see cref="Retention"/>, and puts the rest back at the tail. A table of any size
/// is therefore visited continuously without one unlucky connection paying for all of it.
/// </remarks>
public sealed class SessionDirectory(TimeProvider time)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>How many entries one registration may examine; the work is proportional to this, not to the table.</summary>
    private const int SweepBatch = 256;

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();
    private readonly ConcurrentQueue<Guid> _sweepOrder = new();

    public void Register(Guid correlationId, uint controllerDeviceId, uint hostDeviceId)
    {
        var now = time.GetUtcNow();
        var entry = new Entry(controllerDeviceId, hostDeviceId, now);
        if (_sessions.TryAdd(correlationId, entry))
        {
            _sweepOrder.Enqueue(correlationId);
        }
        else
        {
            // The old queue item still names this id, so replacing the entry needs no second enqueue.
            _sessions[correlationId] = entry;
        }

        SweepExpired(now);
    }

    public bool IsParticipant(Guid correlationId, uint deviceId, string role)
    {
        if (!_sessions.TryGetValue(correlationId, out var entry))
        {
            return false;
        }

        var allowed = role switch
        {
            "controller" => entry.ControllerDeviceId == deviceId,
            "host" => entry.HostDeviceId == deviceId,
            _ => false,
        };
        if (allowed)
        {
            entry.LastReport = time.GetUtcNow();
        }

        return allowed;
    }

    /// <summary>Whether the id is still retained, without refreshing it. For diagnostics and tests.</summary>
    internal bool Contains(Guid correlationId) => _sessions.ContainsKey(correlationId);

    private void SweepExpired(DateTimeOffset now)
    {
        for (var examined = 0; examined < SweepBatch && _sweepOrder.TryDequeue(out var correlationId); examined++)
        {
            if (!_sessions.TryGetValue(correlationId, out var entry))
            {
                // Removed by another registration's sweep after this id was queued.
                continue;
            }

            if (now - entry.LastReport <= Retention)
            {
                _sweepOrder.Enqueue(correlationId);
                continue;
            }

            // Only drop the entry that was examined: a concurrent registration of the same id replaces it, and that
            // fresh one has another seven days. When the replacement won, put the id back in the queue — otherwise the
            // fresh entry would never be visited again.
            var removed = ((ICollection<KeyValuePair<Guid, Entry>>)_sessions)
                .Remove(new KeyValuePair<Guid, Entry>(correlationId, entry));
            if (!removed && _sessions.ContainsKey(correlationId))
            {
                _sweepOrder.Enqueue(correlationId);
            }
        }
    }

    private sealed record Entry(uint ControllerDeviceId, uint HostDeviceId, DateTimeOffset CreatedAt)
    {
        private long _lastReportTicks = CreatedAt.UtcTicks;

        /// <summary>Written from connection threads; ticks move atomically, a <see cref="DateTimeOffset"/> does not.</summary>
        public DateTimeOffset LastReport
        {
            get => new(Interlocked.Read(ref _lastReportTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastReportTicks, value.UtcTicks);
        }
    }
}
