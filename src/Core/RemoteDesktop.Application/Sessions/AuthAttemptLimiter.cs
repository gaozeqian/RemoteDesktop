using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Limits online password guessing per controller device. There is deliberately no global failure budget: an
/// unauthenticated caller can rotate device identities, so a global lock would let it deny every legitimate
/// controller without ever knowing a password. Concurrency is bounded separately by connection admission.
/// </summary>
public sealed class AuthAttemptLimiter(TimeProvider time)
{
    public const int MaxFailuresPerDevice = 5;

    public static readonly TimeSpan DeviceWindow = TimeSpan.FromMinutes(5);

    private readonly Dictionary<DeviceId, Window> _devices = [];
    private readonly Lock _lock = new();

    public bool IsBlocked(DeviceId device)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            return _devices.TryGetValue(device, out var window) && Current(ref window, now, DeviceWindow).Failures >= MaxFailuresPerDevice;
        }
    }

    public void RecordFailure(DeviceId device)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
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
