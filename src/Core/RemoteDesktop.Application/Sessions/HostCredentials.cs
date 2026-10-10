using RemoteDesktop.Application.Security;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Sessions;

/// <summary>What a controller must know to connect to this device, and what it may do once connected.</summary>
public sealed class HostCredentials(TimeProvider? time = null)
{
    /// <summary>
    /// How long the temporary password stays the same before the next look at it replaces it.
    /// </summary>
    /// <remarks>
    /// It is shown on screen and read out loud, so it has to survive the phone call; it is also the one password
    /// somebody may have photographed or pasted into a chat, so it should not survive the day. The fixed password is
    /// a separate thing and does not expire — lasting is what it is for. See docs/security.md.
    /// </remarks>
    public static readonly TimeSpan TemporaryPasswordLifetime = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile string _temporaryPassword = TemporaryPasswordGenerator.Generate();
    private long _rotatedAtTicks = (time ?? TimeProvider.System).GetUtcNow().UtcTicks;
    private volatile string? _fixedPassword;

    /// <summary>
    /// Shown next to the device id. Changes on demand, and on its own once it is older than
    /// <see cref="TemporaryPasswordLifetime"/>.
    /// </summary>
    public string TemporaryPassword => CurrentTemporaryPassword();

    /// <summary>Optional long-lived password for unattended access; <c>null</c> disables it.</summary>
    public string? FixedPassword
    {
        get => _fixedPassword;
        set
        {
            _fixedPassword = string.IsNullOrEmpty(value) ? null : value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Upper bound of what any controller is granted (the host's policy). All capabilities including the camera are
    /// allowed by default (see <see cref="HostPolicy"/>).
    /// </summary>
    public Permissions AllowedPermissions { get; set; } = HostPolicy.Default;

    public event EventHandler? Changed;

    public void RotateTemporaryPassword()
    {
        _temporaryPassword = TemporaryPasswordGenerator.Generate();
        Volatile.Write(ref _rotatedAtTicks, _time.GetUtcNow().UtcTicks);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Verify(ReadOnlySpan<byte> proof, ReadOnlySpan<byte> nonce, string hostFingerprint, string controllerFingerprint)
    {
        // Evaluate both so the time taken does not reveal which password is configured. The temporary one is read
        // through the same door as the one the interface shows, so a password that has aged out no longer opens
        // anything even if nobody has looked at the window since.
        var temporary = PasswordProof.Verify(proof, CurrentTemporaryPassword(), nonce, hostFingerprint, controllerFingerprint);
        var fixedPassword = _fixedPassword;
        var fixedMatch = fixedPassword is not null && PasswordProof.Verify(proof, fixedPassword, nonce, hostFingerprint, controllerFingerprint);
        return temporary | fixedMatch;
    }

    private string CurrentTemporaryPassword()
    {
        // Whoever arrives after the lifetime is up replaces it. Two callers racing only means one of the two new
        // passwords is the one that stays; neither is accepted for longer than it should be.
        if (_time.GetUtcNow() - new DateTimeOffset(Volatile.Read(ref _rotatedAtTicks), TimeSpan.Zero) >= TemporaryPasswordLifetime)
        {
            RotateTemporaryPassword();
        }

        return _temporaryPassword;
    }
}
