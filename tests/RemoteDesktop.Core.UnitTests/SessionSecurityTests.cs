using System.Security.Cryptography;
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

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
