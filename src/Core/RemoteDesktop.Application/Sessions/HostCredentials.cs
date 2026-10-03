using RemoteDesktop.Application.Security;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Sessions;

/// <summary>What a controller must know to connect to this device, and what it may do once connected.</summary>
public sealed class HostCredentials
{
    private volatile string _temporaryPassword = TemporaryPasswordGenerator.Generate();
    private volatile string? _fixedPassword;

    /// <summary>Shown next to the device id; rotated on demand.</summary>
    public string TemporaryPassword => _temporaryPassword;

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

    /// <summary>Upper bound of what any controller is granted (the host's policy).</summary>
    public Permissions AllowedPermissions { get; set; } = Permissions.All;

    public event EventHandler? Changed;

    public void RotateTemporaryPassword()
    {
        _temporaryPassword = TemporaryPasswordGenerator.Generate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Verify(ReadOnlySpan<byte> proof, ReadOnlySpan<byte> nonce, string hostFingerprint, string controllerFingerprint)
    {
        // Evaluate both so the time taken does not reveal which password is configured.
        var temporary = PasswordProof.Verify(proof, _temporaryPassword, nonce, hostFingerprint, controllerFingerprint);
        var fixedPassword = _fixedPassword;
        var fixedMatch = fixedPassword is not null && PasswordProof.Verify(proof, fixedPassword, nonce, hostFingerprint, controllerFingerprint);
        return temporary | fixedMatch;
    }
}
