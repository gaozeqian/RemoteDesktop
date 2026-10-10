using System.Security.Cryptography;
using RemoteDesktop.Application.Security;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Core.UnitTests;

public class PasswordProofTests
{
    private const string HostFp = "AA11";
    private const string ControllerFp = "BB22";

    private static readonly byte[] Nonce = RandomNumberGenerator.GetBytes(PasswordProof.NonceSize);

    [Fact]
    public void Proof_verifies_with_the_same_inputs_and_ignores_fingerprint_case()
    {
        var proof = PasswordProof.Compute("secret", Nonce, HostFp, ControllerFp);

        Assert.True(PasswordProof.Verify(proof, "secret", Nonce, HostFp.ToLowerInvariant(), ControllerFp));
    }

    [Fact]
    public void Proof_is_bound_to_password_nonce_and_both_certificates()
    {
        var proof = PasswordProof.Compute("secret", Nonce, HostFp, ControllerFp);

        Assert.False(PasswordProof.Verify(proof, "Secret", Nonce, HostFp, ControllerFp));
        Assert.False(PasswordProof.Verify(proof, "secret", RandomNumberGenerator.GetBytes(PasswordProof.NonceSize), HostFp, ControllerFp));
        Assert.False(PasswordProof.Verify(proof, "secret", Nonce, "CC33", ControllerFp)); // man in the middle as host
        Assert.False(PasswordProof.Verify(proof, "secret", Nonce, HostFp, "CC33")); // replay by another controller
    }

    /// <summary>
    /// The work factor is a security parameter rather than a tuning knob: it is what stands between a captured proof
    /// and the password behind it. Lowering it should be a deliberate act with the remarks on
    /// <see cref="PasswordProof"/> in front of you, not an edit that slips through with a performance change.
    /// </summary>
    /// <summary>
    /// What is shown on the machine is read out loud and typed by hand, so it is short by nature — and what stands
    /// between a captured proof and the machine is exactly how short. Twelve characters of a 33-character alphabet
    /// is around 2^60, which is out of reach of the hardware somebody would rent for it; the eight it used to be was
    /// not. Shortening it again should be a deliberate act.
    /// </summary>
    [Fact]
    public void What_is_shown_on_the_machine_is_long_enough_to_be_worth_something()
    {
        var password = TemporaryPasswordGenerator.Generate();

        Assert.Equal(TemporaryPasswordGenerator.DefaultLength, password.Length);
        Assert.True(password.Length >= 12, $"The temporary password is down to {password.Length} characters; see TemporaryPasswordGenerator.");
    }

    [Fact]
    public void Turning_the_password_into_a_key_costs_something()
    {
        Assert.True(
            PasswordProof.KeyDerivationIterations >= 600_000,
            $"The key derivation was lowered to {PasswordProof.KeyDerivationIterations} rounds; see the remarks on PasswordProof before changing it.");
    }

    [Fact]
    public void Host_credentials_accept_temporary_and_fixed_password()
    {
        var credentials = new HostCredentials { FixedPassword = "fixed" };

        Assert.True(credentials.Verify(PasswordProof.Compute(credentials.TemporaryPassword, Nonce, HostFp, ControllerFp), Nonce, HostFp, ControllerFp));
        Assert.True(credentials.Verify(PasswordProof.Compute("fixed", Nonce, HostFp, ControllerFp), Nonce, HostFp, ControllerFp));

        var old = credentials.TemporaryPassword;
        credentials.RotateTemporaryPassword();
        Assert.False(credentials.Verify(PasswordProof.Compute(old, Nonce, HostFp, ControllerFp), Nonce, HostFp, ControllerFp));
    }

    /// <summary>
    /// The temporary password is the one somebody may have read over a shoulder or into a chat window, so it stops
    /// being the password on its own. Nobody has to be at the machine for that to happen: the interface asking for
    /// the status is enough, and so is a controller trying its luck.
    /// </summary>
    [Fact]
    public void A_temporary_password_stops_working_once_it_is_old_enough()
    {
        var time = new ManualTime();
        var credentials = new HostCredentials(time);
        var shown = credentials.TemporaryPassword;

        time.Advance(HostCredentials.TemporaryPasswordLifetime - TimeSpan.FromMinutes(1));
        Assert.True(credentials.Verify(PasswordProof.Compute(shown, Nonce, HostFp, ControllerFp), Nonce, HostFp, ControllerFp));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.NotEqual(shown, credentials.TemporaryPassword);
        Assert.False(credentials.Verify(PasswordProof.Compute(shown, Nonce, HostFp, ControllerFp), Nonce, HostFp, ControllerFp));
    }

    [Fact]
    public void A_fixed_password_does_not_expire()
    {
        var time = new ManualTime();
        var credentials = new HostCredentials(time) { FixedPassword = "unattended-password" };

        time.Advance(TimeSpan.FromDays(30));

        Assert.True(credentials.Verify(PasswordProof.Compute("unattended-password", Nonce, HostFp, ControllerFp), Nonce, HostFp, ControllerFp));
    }
}

public class AuthAttemptLimiterTests
{
    private readonly ManualTime _time = new();

    [Fact]
    public void Device_is_blocked_after_max_failures_and_released_after_the_window()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);

        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice; i++)
        {
            Assert.False(limiter.IsBlocked(device));
            limiter.RecordFailure(device);
        }

        Assert.True(limiter.IsBlocked(device));
        Assert.False(limiter.IsBlocked(new DeviceId(222_222_222)));

        _time.Advance(AuthAttemptLimiter.DeviceWindow + TimeSpan.FromSeconds(1));
        Assert.False(limiter.IsBlocked(device));
    }

    [Fact]
    public void Many_devices_failing_trigger_the_global_limit()
    {
        var limiter = new AuthAttemptLimiter(_time);
        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresGlobal; i++)
        {
            limiter.RecordFailure(new DeviceId(300_000_000 + i));
        }

        Assert.True(limiter.IsBlocked(new DeviceId(999_999_999)));
    }

}
