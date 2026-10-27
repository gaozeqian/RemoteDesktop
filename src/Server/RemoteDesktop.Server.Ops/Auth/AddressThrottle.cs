using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Auth;

/// <summary>
/// A second, looser failure budget for one source address, shared by every user name tried from it.
/// </summary>
/// <remarks>
/// The per-name <see cref="LoginThrottle"/> stops guessing one account. It does not stop a spray across thousands of
/// names, and that spray is what used to fill the throttle table and lock every new key. This budget is deliberately
/// looser because a NAT or a reverse proxy can put several people behind one address; a successful sign-in does not
/// reset it, so the spray cannot clear its own record by occasionally guessing one valid account.
/// </remarks>
public sealed class AddressThrottle(TimeProvider time)
{
    private readonly LoginThrottle _inner = new(
        time,
        maxFailures: 50,
        lockSeconds: 600,
        maxTrackedKeys: 4096);

    /// <summary>True when this address has spent its shared budget.</summary>
    public bool IsLocked(string? address, out TimeSpan retryAfter) =>
        _inner.IsLocked(LoginThrottle.AddressKey(address), out retryAfter);

    /// <summary>Records one failed sign-in attempt from this address.</summary>
    public void RecordFailure(string? address) => _inner.RecordFailure(LoginThrottle.AddressKey(address));
}
