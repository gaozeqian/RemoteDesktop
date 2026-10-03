using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Limits online password guessing: a few failures lock out one controller device for a while, and a burst of
/// failures from many devices (an attacker rotating identities) locks out everyone briefly.
/// </summary>
public sealed class AuthAttemptLimiter(TimeProvider time)
{
    public const int MaxFailuresPerDevice = 5;
    public const int MaxFailuresGlobal = 20;

    public static readonly TimeSpan DeviceWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);

    private readonly Dictionary<DeviceId, Window> _devices = [];
    private readonly Lock _lock = new();
    private Window _global;

    public bool IsBlocked(DeviceId device)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (Current(ref _global, now, GlobalWindow).Failures >= MaxFailuresGlobal)
            {
                return true;
            }

            return _devices.TryGetValue(device, out var window) && Current(ref window, now, DeviceWindow).Failures >= MaxFailuresPerDevice;
        }
    }

    public void RecordFailure(DeviceId device)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            Current(ref _global, now, GlobalWindow).Failures++;

            _devices.TryGetValue(device, out var window);
            Current(ref window, now, DeviceWindow).Failures++;
            _devices[device] = window;

            // Keep the table bounded under an identity-rotating attack.
            if (_devices.Count > 10_000)
            {
                foreach (var stale in _devices.Where(d => now - d.Value.Start > DeviceWindow).Select(d => d.Key).ToList())
                {
                    _devices.Remove(stale);
                }
            }
        }
    }

    public void RecordSuccess(DeviceId device)
    {
        lock (_lock)
        {
            _devices.Remove(device);
        }
    }

    private static ref Window Current(ref Window window, DateTimeOffset now, TimeSpan length)
    {
        if (now - window.Start > length)
        {
            window = new Window { Start = now };
        }

        return ref window;
    }

    private struct Window
    {
        public DateTimeOffset Start;
        public int Failures;
    }
}
