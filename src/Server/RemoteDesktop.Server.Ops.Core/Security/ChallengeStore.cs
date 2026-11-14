namespace RemoteDesktop.Server.Ops.Core.Security;

/// <summary>
/// Password-verified, TOTP-pending challenges. In memory: they live five minutes, and a restart cancelling every
/// half-finished sign-in is the correct behaviour rather than a bug.
/// </summary>
public sealed class ChallengeStore(TimeProvider time)
{
    private const int MaxChallenges = 10_000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Records that <paramref name="userId"/> passed the password step; returns the challenge id.</summary>
    public string Create(string userId, out DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        lock (_gate)
        {
            Prune();
            var id = SessionToken.Create();
            expiresAt = time.GetUtcNow() + Lifetime;
            _byId[id] = new Entry { UserId = userId, ExpiresAt = expiresAt };

            // Prune only sees the old count; adding this one may put the store one over. Trim after the insert and
            // keep the challenge that was just created: it is the one the caller is about to return an id for.
            TrimToCapacity(keep: id);
            return id;
        }
    }

    /// <summary>Looks a challenge up without consuming it, so a mistyped code can be retried.</summary>
    public bool TryGet(string? id, out string userId, out DateTimeOffset expiresAt)
    {
        userId = string.Empty;
        expiresAt = default;

        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        lock (_gate)
        {
            Prune();
            if (!_byId.TryGetValue(id, out var entry))
            {
                return false;
            }

            userId = entry.UserId;
            expiresAt = entry.ExpiresAt;
            return true;
        }
    }

    /// <summary>Counts a wrong code. Returns false once the challenge has spent its attempts and was discarded.</summary>
    public bool RecordFailure(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var entry))
            {
                return false;
            }

            entry.Failures++;
            if (entry.Failures < 5)
            {
                return true;
            }

            _byId.Remove(id);
            return false;
        }
    }

    /// <summary>How many challenges are pending. For diagnostics and tests; the cap is the observable invariant.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _byId.Count;
            }
        }
    }

    /// <summary>Consumes a challenge after a successful code.</summary>
    public void Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_gate)
        {
            _byId.Remove(id);
        }
    }

    private void Prune()
    {
        if (_byId.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        List<string>? expired = null;
        foreach (var (id, entry) in _byId)
        {
            if (entry.ExpiresAt <= now)
            {
                (expired ??= []).Add(id);
            }
        }

        if (expired is not null)
        {
            foreach (var id in expired)
            {
                _byId.Remove(id);
            }
        }

        TrimToCapacity();
    }

    /// <summary>
    /// An attacker who can start sign-ins faster than they expire should not grow this without bound. Dropping the
    /// oldest pending challenges only penalises sign-ins already half-finished, never a completed one.
    /// </summary>
    /// <param name="keep">The challenge just created, which must survive the trim so its id can be handed back.</param>
    private void TrimToCapacity(string? keep = null)
    {
        while (_byId.Count > MaxChallenges)
        {
            var oldest = _byId.Where(pair => !string.Equals(pair.Key, keep, StringComparison.Ordinal))
                .MinBy(pair => pair.Value.ExpiresAt)
                .Key;
            _byId.Remove(oldest);
        }
    }

    private sealed class Entry
    {
        public required string UserId { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }

        public int Failures { get; set; }
    }
}
