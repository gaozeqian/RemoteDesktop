using System.Collections.Concurrent;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>Server-minted session ids and the only two devices allowed to report on each session.</summary>
public sealed class SessionDirectory(TimeProvider time)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();

    public void Register(Guid correlationId, uint controllerDeviceId, uint hostDeviceId)
    {
        var now = time.GetUtcNow();
        _sessions[correlationId] = new Entry(controllerDeviceId, hostDeviceId, now);

        // No timer per session. Registration occasionally reclaims entries for which neither participant has reported.
        foreach (var stale in _sessions.Where(pair => now - pair.Value.LastReport > Retention).Select(pair => pair.Key))
        {
            _sessions.TryRemove(stale, out _);
        }
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

    private sealed record Entry(uint ControllerDeviceId, uint HostDeviceId, DateTimeOffset CreatedAt)
    {
        public DateTimeOffset LastReport { get; set; } = CreatedAt;
    }
}
