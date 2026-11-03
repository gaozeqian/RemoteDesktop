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

    /// <summary>
    /// The most devices remembered at once. Past this an identity-rotating caller could grow the table without bound;
    /// see <see cref="RecordFailure"/> for what happens at the ceiling.
    /// </summary>
    public const int MaxTrackedDevices = 10_000;

    public static readonly TimeSpan DeviceWindow = TimeSpan.FromMinutes(5);

    /// <summary>How often expired entries are dropped. A scan per failure would make the attack itself the cost.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<DeviceId, Window> _devices = [];
    private readonly Lock _lock = new();
    private DateTimeOffset _nextSweep;

    // Set when a new device could not be remembered because every slot holds a device that is locked out. While it is
    // set, a device the table does not know is refused: forgetting its failures would give it unlimited guesses.
    private bool _saturated;

    public bool IsBlocked(DeviceId device)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_devices.TryGetValue(device, out var window))
            {
                return Current(ref window, now, DeviceWindow).Failures >= MaxFailuresPerDevice;
            }

            if (_saturated && now >= _nextSweep)
            {
                // Nobody records failures for a device that is refused up front, so the flag has to be able to clear
                // on its own once the locked-out devices age out.
                Sweep(now);
                _nextSweep = now + SweepInterval;
            }

            return _saturated;
        }
    }

    public void RecordFailure(DeviceId device)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (now >= _nextSweep)
            {
                Sweep(now);
                _nextSweep = now + SweepInterval;
            }

            if (!_devices.TryGetValue(device, out var window) && _devices.Count >= MaxTrackedDevices)
            {
                // A new device and no room. Make room by dropping expired entries, then the oldest devices that are not
                // locked out - they have the least to lose, and a locked-out device keeps its lock. Evicting in a batch
                // keeps this off the hot path: the next several hundred new devices find room without another scan.
                Sweep(now);
                if (_devices.Count >= MaxTrackedDevices)
                {
                    EvictOldestUnblocked(now, MaxTrackedDevices / 10);
                }

                if (_devices.Count >= MaxTrackedDevices)
                {
                    // Every slot is a locked-out device (it took 5 failed handshakes each to get here). Not tracking
                    // this one would let it guess forever, so unknown devices are refused until room appears.
                    _saturated = true;
                    return;
                }
            }

            Current(ref window, now, DeviceWindow).Failures++;
            _devices[device] = window;
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var stale in _devices.Where(d => now - d.Value.Start > DeviceWindow).Select(d => d.Key).ToList())
        {
            _devices.Remove(stale);
        }

        if (_devices.Count < MaxTrackedDevices)
        {
            _saturated = false;
        }
    }

    private void EvictOldestUnblocked(DateTimeOffset now, int count)
    {
        var victims = _devices
            .Where(d => d.Value.Failures < MaxFailuresPerDevice || now - d.Value.Start > DeviceWindow)
            .OrderBy(d => d.Value.Start)
            .Take(count)
            .Select(d => d.Key)
            .ToList();
        foreach (var victim in victims)
        {
            _devices.Remove(victim);
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
