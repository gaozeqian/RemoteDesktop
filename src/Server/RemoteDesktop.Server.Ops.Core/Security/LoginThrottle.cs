using System.Globalization;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// Counts failed sign-ins per key (address plus user name) and locks the key for a while once it is clear nobody is
/// guessing by accident.
/// </summary>
/// <remarks>
/// In memory on purpose: the console is a single instance and a lockout that survives a restart is
/// also a lockout an operator cannot clear without editing the database. The table of attempts is bounded — an
/// unbounded dictionary keyed by attacker-chosen input is a memory leak with extra steps.
/// </remarks>
public sealed class LoginThrottle(
    TimeProvider time,
    int maxFailures = 5,
    int lockSeconds = 300,
    int maxTrackedKeys = 10_000)
{
    private readonly Dictionary<string, Attempts> _byKey = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeSpan _lockFor = TimeSpan.FromSeconds(lockSeconds);
    private readonly TimeSpan _forgetAfter = TimeSpan.FromHours(1);
    private readonly int _maxTrackedKeys = Math.Max(1, maxTrackedKeys);

    /// <summary>True when <paramref name="key"/> has spent its attempts; <paramref name="retryAfter"/> says for how long.</summary>
    public bool IsLocked(string key, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            var now = time.GetUtcNow();
            Prune(now);

            if (_byKey.TryGetValue(key, out var attempts) && attempts.LockedUntil > now)
            {
                retryAfter = attempts.LockedUntil - now;
                return true;
            }

            // An unknown key is never locked just because the table is full. The old behaviour — lock every new key
            // at capacity — let an unauthenticated attacker send ten thousand user names and stop every sign-in,
            // including the administrator's. Room is made in RecordFailure by evicting the oldest entry instead.
            retryAfter = TimeSpan.Zero;
            return false;
        }
    }

    /// <summary>Records one failed attempt, locking the key once the budget is spent.</summary>
    public void RecordFailure(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            var now = time.GetUtcNow();
            Prune(now);

            if (!_byKey.TryGetValue(key, out var attempts))
            {
                MakeRoom();
                attempts = new Attempts();
                _byKey[key] = attempts;
            }

            attempts.Failures++;
            attempts.LastSeen = now;
            if (attempts.Failures >= maxFailures)
            {
                attempts.LockedUntil = now + _lockFor;
                attempts.Failures = 0;
            }
        }
    }

    /// <summary>Forgets a key after a successful sign-in, so one bad evening does not follow someone into next week.</summary>
    public void Reset(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            _byKey.Remove(key);
        }
    }

    /// <summary>
    /// Address and user name, as one opaque key. Neither half is logged from here.
    /// </summary>
    /// <remarks>
    /// The user name is folded the same way the account lookup folds it (trim, upper-case). The throttle and the
    /// lookup have to agree on what "the same account" means: keying on the raw spelling let "admin", "Admin" and
    /// "admin " share one account but not one failure budget, so the lockout could be sidestepped by respelling the
    /// name on every attempt.
    /// </remarks>
    public static string KeyFor(string? address, string? username) =>
        string.Concat(address ?? "-", "\n", NormalizeUsername(username));

    /// <summary>
    /// The budget for one address, independent of the user name. A spray across thousands of names spends this one,
    /// while the per-name key above stays small enough to see the account it belongs to.
    /// </summary>
    public static string AddressKey(string? address) => string.Concat("addr\n", address ?? "-");

    private static string NormalizeUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? "-" : username.Trim().ToUpperInvariant();

    /// <summary>
    /// Evicts one entry when the table is at capacity. The oldest entry with no live lockout goes first; if every
    /// entry is currently locked, the oldest lockout goes anyway. Evicting a lockout is the lesser evil: the address
    /// budget is the backstop that keeps one client from clearing its own lockout by spraying names.
    /// </summary>
    private void MakeRoom()
    {
        if (_byKey.Count < _maxTrackedKeys)
        {
            return;
        }

        var now = time.GetUtcNow();
        string? victim = null;
        var oldest = DateTimeOffset.MaxValue;
        var oldestOverall = DateTimeOffset.MaxValue;
        string? fallback = null;

        foreach (var (key, attempts) in _byKey)
        {
            if (attempts.LastSeen < oldestOverall)
            {
                oldestOverall = attempts.LastSeen;
                fallback = key;
            }

            if (attempts.LockedUntil <= now && attempts.LastSeen < oldest)
            {
                oldest = attempts.LastSeen;
                victim = key;
            }
        }

        _byKey.Remove(victim ?? fallback!);
    }

    private void Prune(DateTimeOffset now)
    {
        if (_byKey.Count == 0)
        {
            return;
        }

        List<string>? expired = null;
        foreach (var (key, attempts) in _byKey)
        {
            if (attempts.LockedUntil <= now && now - attempts.LastSeen > _forgetAfter)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _byKey.Remove(key);
        }
    }

    private sealed class Attempts
    {
        public int Failures { get; set; }

        public DateTimeOffset LockedUntil { get; set; }

        public DateTimeOffset LastSeen { get; set; }
    }
}
