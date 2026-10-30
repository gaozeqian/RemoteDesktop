using System.Security.Cryptography;
using RemoteDesktop.Application.Security;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Core.UnitTests;

public class PasswordAuthenticatorTests
{
    private const string HostFp = "AA11";
    private const string ControllerFp = "BB22";

    [Fact]
    public void Temporary_password_completes_a_mutually_authenticated_exchange()
    {
        var credentials = new HostCredentials();
        var password = credentials.TemporaryPassword;
        var challenges = credentials.BeginAuthentication(HostFp, ControllerFp);

        Assert.Equal(HostCredentials.CredentialSlotCount, challenges.Count);
        var attempt = PasswordAuthenticator.Respond(password, challenges[0].Salt, challenges[0].ServerPublic, HostFp, ControllerFp);

        Assert.True(PasswordAuthenticator.VerifyHost(challenges[0], attempt.ClientPublic, attempt.ClientEvidence, out var serverEvidence));
        Assert.True(PasswordAuthenticator.VerifyServer(attempt, serverEvidence));
        Assert.False(PasswordAuthenticator.VerifyServer(attempt, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void Temporary_and_fixed_passwords_use_different_slots()
    {
        var credentials = new HostCredentials { FixedPassword = "fixed-password" };
        var challenges = credentials.BeginAuthentication(HostFp, ControllerFp);

        var fixedAttempt = PasswordAuthenticator.Respond("fixed-password", challenges[1].Salt, challenges[1].ServerPublic, HostFp, ControllerFp);
        Assert.True(PasswordAuthenticator.VerifyHost(challenges[1], fixedAttempt.ClientPublic, fixedAttempt.ClientEvidence, out _));

        var wrongSlot = PasswordAuthenticator.Respond("fixed-password", challenges[0].Salt, challenges[0].ServerPublic, HostFp, ControllerFp);
        Assert.False(PasswordAuthenticator.VerifyHost(challenges[0], wrongSlot.ClientPublic, wrongSlot.ClientEvidence, out _));
    }

    [Fact]
    public void A_disabled_fixed_password_cannot_be_entered_as_empty()
    {
        var credentials = new HostCredentials();
        var challenges = credentials.BeginAuthentication(HostFp, ControllerFp);
        var empty = PasswordAuthenticator.Respond(string.Empty, challenges[1].Salt, challenges[1].ServerPublic, HostFp, ControllerFp);

        Assert.False(PasswordAuthenticator.VerifyHost(challenges[1], empty.ClientPublic, empty.ClientEvidence, out _));
    }

    [Fact]
    public void The_exchange_is_bound_to_the_password_and_both_certificates()
    {
        var credentials = new HostCredentials();
        var password = credentials.TemporaryPassword;

        Assert.False(CanAuthenticate(credentials, password, HostFp, ControllerFp, "not-the-password", HostFp, ControllerFp));
        Assert.False(CanAuthenticate(credentials, password, HostFp, ControllerFp, password, "CC33", ControllerFp));
        Assert.False(CanAuthenticate(credentials, password, HostFp, ControllerFp, password, HostFp, "CC33"));
        Assert.True(CanAuthenticate(credentials, password, HostFp, ControllerFp, password, HostFp, ControllerFp));

        static bool CanAuthenticate(
            HostCredentials credentials,
            string password,
            string challengeHost,
            string challengeController,
            string supplied,
            string responseHost,
            string responseController)
        {
            _ = password;
            var challenge = credentials.BeginAuthentication(challengeHost, challengeController)[0];
            var attempt = PasswordAuthenticator.Respond(supplied, challenge.Salt, challenge.ServerPublic, responseHost, responseController);
            return PasswordAuthenticator.VerifyHost(challenge, attempt.ClientPublic, attempt.ClientEvidence, out _);
        }
    }

    [Fact]
    public void Every_challenge_has_fresh_salt_and_public_values()
    {
        var credentials = new HostCredentials();
        var first = credentials.BeginAuthentication(HostFp, ControllerFp)[0];
        var second = credentials.BeginAuthentication(HostFp, ControllerFp)[0];

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.ServerPublic, second.ServerPublic);
    }

    /// <summary>
    /// What is shown on the machine is read out loud and typed by hand, so it is short by nature — and a PAKE still
    /// only makes each online guess cost one handshake, not an offline brute-force. Shortening it again should be a
    /// deliberate act.
    /// </summary>
    [Fact]
    public void What_is_shown_on_the_machine_is_long_enough_to_be_worth_something()
    {
        var password = TemporaryPasswordGenerator.Generate();

        Assert.Equal(TemporaryPasswordGenerator.DefaultLength, password.Length);
        Assert.True(password.Length >= 12, $"The temporary password is down to {password.Length} characters; see TemporaryPasswordGenerator.");
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
        var before = credentials.BeginAuthentication(HostFp, ControllerFp);
        var accepted = PasswordAuthenticator.Respond(shown, before[0].Salt, before[0].ServerPublic, HostFp, ControllerFp);
        Assert.True(PasswordAuthenticator.VerifyHost(before[0], accepted.ClientPublic, accepted.ClientEvidence, out _));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.NotEqual(shown, credentials.TemporaryPassword);
        var after = credentials.BeginAuthentication(HostFp, ControllerFp);
        var expired = PasswordAuthenticator.Respond(shown, after[0].Salt, after[0].ServerPublic, HostFp, ControllerFp);
        Assert.False(PasswordAuthenticator.VerifyHost(after[0], expired.ClientPublic, expired.ClientEvidence, out _));
    }

    [Fact]
    public void A_fixed_password_does_not_expire()
    {
        var time = new ManualTime();
        var credentials = new HostCredentials(time) { FixedPassword = "unattended-password" };

        time.Advance(TimeSpan.FromDays(30));

        var challenges = credentials.BeginAuthentication(HostFp, ControllerFp);
        var attempt = PasswordAuthenticator.Respond("unattended-password", challenges[1].Salt, challenges[1].ServerPublic, HostFp, ControllerFp);
        Assert.True(PasswordAuthenticator.VerifyHost(challenges[1], attempt.ClientPublic, attempt.ClientEvidence, out _));
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
    public void Many_devices_failing_do_not_lock_a_legitimate_device_out()
    {
        // An unauthenticated attacker can rotate device identities. A global failure budget would therefore be a
        // denial-of-service switch that needs no password; only the device that actually failed is locked.
        var limiter = new AuthAttemptLimiter(_time);
        for (var i = 0; i < 1000; i++)
        {
            limiter.RecordFailure(new DeviceId(300_000_000 + i));
        }

        Assert.False(limiter.IsBlocked(new DeviceId(999_999_999)));
    }

}
