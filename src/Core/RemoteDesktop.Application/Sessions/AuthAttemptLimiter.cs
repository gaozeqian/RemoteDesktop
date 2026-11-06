using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// Limits online password guessing per controller device. There is deliberately no global failure budget: an
/// unauthenticated caller can rotate device identities, so a global lock would let it deny every legitimate
/// controller without ever knowing a password. Concurrency is bounded separately by connection admission.
/// <para>
/// A handshake takes an attempt with <see cref="TryReserve"/> <em>before</em> it asks for the password and settles it
/// afterwards, so concurrent handshakes from one device are counted as they start rather than as they fail: the
/// recorded failures plus the handshakes still in flight never exceed <see cref="MaxFailuresPerDevice"/>.
/// </para>
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
    private int _pending;

    // Set when a new device could not be remembered because every slot holds a device that is locked out. While it is
    // set, a device the table does not know is refused: forgetting its failures would give it unlimited guesses.
    private bool _saturated;

    /// <summary>
    /// Handshakes that hold a reservation and have not settled it yet, across all devices. Exposed so that tests can
    /// prove that no path leaks a reservation; a leaked one would occupy a device's budget until the process ends.
    /// </summary>
    public int PendingReservations
    {
        get
        {
            lock (_lock)
            {
                return _pending;
            }
        }
    }

    /// <summary>
    /// Takes one of the device's attempts for a handshake that is about to start. Refused (null) when the device is
    /// locked out, when its recorded failures plus the handshakes in flight already reach
    /// <see cref="MaxFailuresPerDevice"/>, or when the device is unknown and the table has no room for it. The caller
    /// must settle the returned reservation: <see cref="AuthAttemptReservation.RecordFailure"/> once the controller
    /// has shown (wrong) password evidence, <see cref="AuthAttemptReservation.RecordSuccess"/> when it was right, and
    /// otherwise disposing it hands the attempt back without counting a failure.
    /// </summary>
    public AuthAttemptReservation? TryReserve(DeviceId device)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (now >= _nextSweep)
            {
                Sweep(now);
                _nextSweep = now + SweepInterval;
            }

            if (_devices.TryGetValue(device, out var window))
            {
                if (Current(ref window, now, DeviceWindow).Failures + window.Pending >= MaxFailuresPerDevice)
                {
                    return null;
                }
            }
            else
            {
                // A reservation needs an entry to live in, and unlike a failure it cannot be skipped when the table is
                // full: a handshake the table does not know about is one the limiter cannot count.
                if (_saturated || (_devices.Count >= MaxTrackedDevices && !MakeRoom(now)))
                {
                    return null;
                }

                window = new Window { Start = now };
            }

            window.Pending++;
            _devices[device] = window;
            _pending++;
            return new AuthAttemptReservation(this, device);
        }
    }

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

            // A new device and no room. If none can be made, every slot is a locked-out device (it took 5 failed
            // handshakes each to get here) or one with a handshake in flight. Not tracking this one would let it guess
            // forever, so unknown devices are refused until room appears.
            if (!_devices.TryGetValue(device, out var window) && _devices.Count >= MaxTrackedDevices && !MakeRoom(now))
            {
                return;
            }

            Current(ref window, now, DeviceWindow).Failures++;
            _devices[device] = window;
        }
    }

    /// <summary>
    /// Makes room for a new device: drops expired entries, then the oldest devices that are not locked out - they have
    /// the least to lose, and a locked-out device keeps its lock. A device with a handshake in flight is never dropped,
    /// because forgetting it would silently return its reservations to the budget. Evicting in a batch keeps this off
    /// the hot path: the next several hundred new devices find room without another scan. Returns false, and raises
    /// the saturation flag, when every slot is locked out or in flight.
    /// </summary>
    private bool MakeRoom(DateTimeOffset now)
    {
        Sweep(now);
        if (_devices.Count >= MaxTrackedDevices)
        {
            EvictOldestUnblocked(now, MaxTrackedDevices / 10);
        }

        if (_devices.Count >= MaxTrackedDevices)
        {
            _saturated = true;
            return false;
        }

        return true;
    }

    private void Sweep(DateTimeOffset now)
    {
        // An entry with a handshake in flight stays however old it is: the reservation does not expire with the window.
        foreach (var stale in _devices.Where(d => d.Value.Pending == 0 && now - d.Value.Start > DeviceWindow).Select(d => d.Key).ToList())
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
            .Where(d => d.Value.Pending == 0 && (d.Value.Failures < MaxFailuresPerDevice || now - d.Value.Start > DeviceWindow))
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
            if (!_devices.TryGetValue(device, out var window))
            {
                return;
            }

            if (window.Pending == 0)
            {
                _devices.Remove(device);
                return;
            }

            // Other handshakes of this device are still in flight: they keep their reservations, only the failures go.
            window.Failures = 0;
            _devices[device] = window;
        }
    }

    /// <summary>Settles one reservation. Called by <see cref="AuthAttemptReservation"/>, once per reservation.</summary>
    internal void Settle(DeviceId device, AuthAttemptOutcome outcome)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            _pending--;

            // The entry is pinned by the reservation (never swept or evicted), so it is there.
            if (!_devices.TryGetValue(device, out var window))
            {
                return;
            }

            window.Pending--;
            Current(ref window, now, DeviceWindow);
            switch (outcome)
            {
                case AuthAttemptOutcome.Failure:
                    window.Failures++;
                    break;
                case AuthAttemptOutcome.Success:
                    window.Failures = 0;
                    break;
            }

            if (window.Pending == 0 && window.Failures == 0)
            {
                // Nothing left to remember about this device, and a slot is worth more than an empty entry.
                _devices.Remove(device);
            }
            else
            {
                _devices[device] = window;
            }
        }
    }

    private static ref Window Current(ref Window window, DateTimeOffset now, TimeSpan length)
    {
        if (now - window.Start > length)
        {
            // The window is over, the handshakes still in flight are not.
            window = new Window { Start = now, Pending = window.Pending };
        }

        return ref window;
    }

    private struct Window
    {
        public DateTimeOffset Start;
        public int Failures;

        // Reserved attempts whose handshake has not finished. Kept apart from Failures because they neither expire
        // with the window nor count as a failure until the handshake says so.
        public int Pending;
    }
}

internal enum AuthAttemptOutcome
{
    Released,
    Failure,
    Success,
}

/// <summary>
/// One attempt taken from <see cref="AuthAttemptLimiter"/> for a handshake in progress. Settled exactly once: by
/// <see cref="RecordFailure"/>, <see cref="RecordSuccess"/>, or - if the handshake ended before the controller showed
/// any password evidence, whatever the reason - by <see cref="Dispose"/>, which gives the attempt back without
/// counting a failure. Calls after the first settlement do nothing, so a <c>using</c> around the handshake is enough
/// to keep every path, exceptions and cancellation included, from leaking the attempt.
/// </summary>
public sealed class AuthAttemptReservation : IDisposable
{
    private readonly AuthAttemptLimiter _limiter;
    private readonly DeviceId _device;
    private int _settled;

    internal AuthAttemptReservation(AuthAttemptLimiter limiter, DeviceId device)
    {
        _limiter = limiter;
        _device = device;
    }

    /// <summary>The controller's password evidence was wrong: the attempt becomes a recorded failure.</summary>
    public void RecordFailure() => Settle(AuthAttemptOutcome.Failure);

    /// <summary>The password was right: the attempt is given back and the device's recorded failures are cleared.</summary>
    public void RecordSuccess() => Settle(AuthAttemptOutcome.Success);

    /// <summary>Gives the attempt back without counting a failure, unless it was settled already.</summary>
    public void Dispose() => Settle(AuthAttemptOutcome.Released);

    private void Settle(AuthAttemptOutcome outcome)
    {
        if (Interlocked.Exchange(ref _settled, 1) == 0)
        {
            _limiter.Settle(_device, outcome);
        }
    }
}
