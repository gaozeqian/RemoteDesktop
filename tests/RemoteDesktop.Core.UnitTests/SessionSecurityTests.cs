using System.Security.Cryptography;
using RemoteDesktop.Application.Security;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.TestSupport;

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

    /// <summary>
    /// The challenge class documents that one challenge is consumed by exactly one verification. Without the guard,
    /// the same SRP server state could be advanced by a second response.
    /// </summary>
    [Fact]
    public void A_challenge_cannot_be_verified_twice()
    {
        var credentials = new HostCredentials();
        var challenge = credentials.BeginAuthentication(HostFp, ControllerFp)[0];
        var attempt = PasswordAuthenticator.Respond(credentials.TemporaryPassword, challenge.Salt, challenge.ServerPublic, HostFp, ControllerFp);

        Assert.True(PasswordAuthenticator.VerifyHost(challenge, attempt.ClientPublic, attempt.ClientEvidence, out _));
        Assert.False(PasswordAuthenticator.VerifyHost(challenge, attempt.ClientPublic, attempt.ClientEvidence, out _));
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

        Assert.False(CanAuthenticate(credentials, HostFp, ControllerFp, "not-the-password", HostFp, ControllerFp));
        Assert.False(CanAuthenticate(credentials, HostFp, ControllerFp, password, "CC33", ControllerFp));
        Assert.False(CanAuthenticate(credentials, HostFp, ControllerFp, password, HostFp, "CC33"));
        Assert.True(CanAuthenticate(credentials, HostFp, ControllerFp, password, HostFp, ControllerFp));

        static bool CanAuthenticate(
            HostCredentials credentials,
            string challengeHost,
            string challengeController,
            string supplied,
            string responseHost,
            string responseController)
        {
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
        var time = new TestClock();
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
        var time = new TestClock();
        var credentials = new HostCredentials(time) { FixedPassword = "unattended-password" };

        time.Advance(TimeSpan.FromDays(30));

        var challenges = credentials.BeginAuthentication(HostFp, ControllerFp);
        var attempt = PasswordAuthenticator.Respond("unattended-password", challenges[1].Salt, challenges[1].ServerPublic, HostFp, ControllerFp);
        Assert.True(PasswordAuthenticator.VerifyHost(challenges[1], attempt.ClientPublic, attempt.ClientEvidence, out _));
    }

    // The host must treat anything a controller sends as hostile. A malformed public value or evidence is a failed
    // authentication (false), never an exception out of the protocol layer.
    private static (HostPasswordChallenge Challenge, PasswordAttempt Attempt) ValidExchange()
    {
        var credentials = new HostCredentials();
        var challenge = credentials.BeginAuthentication(HostFp, ControllerFp)[0];
        var attempt = PasswordAuthenticator.Respond(
            credentials.TemporaryPassword, challenge.Salt, challenge.ServerPublic, HostFp, ControllerFp);
        return (challenge, attempt);
    }

    private static Org.BouncyCastle.Math.BigInteger GroupModulus() =>
        Org.BouncyCastle.Crypto.Agreement.Srp.Srp6StandardGroups.rfc5054_3072.N;

    [Fact]
    public void The_host_rejects_a_client_public_value_of_zero()
    {
        var (challenge, attempt) = ValidExchange();

        Assert.False(PasswordAuthenticator.VerifyHost(challenge, new byte[] { 0 }, attempt.ClientEvidence, out var serverEvidence));
        Assert.Empty(serverEvidence);
    }

    [Fact]
    public void The_host_rejects_an_all_zero_client_public_value_of_full_width()
    {
        var (challenge, attempt) = ValidExchange();

        Assert.False(PasswordAuthenticator.VerifyHost(challenge, new byte[384], attempt.ClientEvidence, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_host_rejects_a_client_public_value_that_is_a_multiple_of_the_group_modulus(int multiple)
    {
        // A mod N == 0 makes the shared secret independent of the password, so SRP requires refusing it.
        var (challenge, attempt) = ValidExchange();
        var value = GroupModulus().Multiply(Org.BouncyCastle.Math.BigInteger.ValueOf(multiple)).ToByteArrayUnsigned();

        Assert.True(value.Length <= PasswordAuthenticator.MaxPublicValueSize);
        Assert.False(PasswordAuthenticator.VerifyHost(challenge, value, attempt.ClientEvidence, out var serverEvidence));
        Assert.Empty(serverEvidence);
    }

    [Fact]
    public void The_host_rejects_an_oversized_client_public_value()
    {
        var (challenge, attempt) = ValidExchange();
        var oversized = new byte[PasswordAuthenticator.MaxPublicValueSize + 1];
        RandomNumberGenerator.Fill(oversized);
        oversized[0] |= 1;

        Assert.False(PasswordAuthenticator.VerifyHost(challenge, oversized, attempt.ClientEvidence, out var serverEvidence));
        Assert.Empty(serverEvidence);
    }

    [Fact]
    public void The_host_rejects_an_empty_client_public_value_or_empty_evidence()
    {
        var (challenge, attempt) = ValidExchange();

        Assert.False(PasswordAuthenticator.VerifyHost(challenge, ReadOnlySpan<byte>.Empty, attempt.ClientEvidence, out _));
        Assert.False(PasswordAuthenticator.VerifyHost(challenge, attempt.ClientPublic, ReadOnlySpan<byte>.Empty, out var serverEvidence));
        Assert.Empty(serverEvidence);
    }

    [Fact]
    public void The_host_rejects_oversized_evidence()
    {
        var (challenge, attempt) = ValidExchange();
        var oversized = new byte[129];
        RandomNumberGenerator.Fill(oversized);
        oversized[0] |= 1;

        Assert.False(PasswordAuthenticator.VerifyHost(challenge, attempt.ClientPublic, oversized, out _));
    }

    [Fact]
    public void The_host_rejects_truncated_client_evidence()
    {
        var (challenge, attempt) = ValidExchange();

        Assert.False(PasswordAuthenticator.VerifyHost(challenge, attempt.ClientPublic, attempt.ClientEvidence.AsSpan(0, attempt.ClientEvidence.Length - 1), out var serverEvidence));
        Assert.Empty(serverEvidence);
    }

    [Fact]
    public void The_host_rejects_a_truncated_client_public_value()
    {
        var (challenge, attempt) = ValidExchange();

        Assert.False(PasswordAuthenticator.VerifyHost(challenge, attempt.ClientPublic.AsSpan(0, attempt.ClientPublic.Length - 1), attempt.ClientEvidence, out var serverEvidence));
        Assert.Empty(serverEvidence);
    }

    [Fact]
    public void The_client_rejects_empty_truncated_zero_or_oversized_server_evidence()
    {
        var (challenge, attempt) = ValidExchange();
        Assert.True(PasswordAuthenticator.VerifyHost(challenge, attempt.ClientPublic, attempt.ClientEvidence, out var evidence));

        Assert.False(PasswordAuthenticator.VerifyServer(attempt, ReadOnlySpan<byte>.Empty));
        Assert.False(PasswordAuthenticator.VerifyServer(attempt, new byte[] { 0 }));
        Assert.False(PasswordAuthenticator.VerifyServer(attempt, evidence.AsSpan(0, evidence.Length - 1)));
        Assert.False(PasswordAuthenticator.VerifyServer(attempt, new byte[129]));
        Assert.True(PasswordAuthenticator.VerifyServer(attempt, evidence));
    }

    [Fact]
    public void The_client_refuses_to_respond_to_a_malformed_challenge()
    {
        var challenge = new HostCredentials().BeginAuthentication(HostFp, ControllerFp)[0];

        Assert.Throws<ArgumentException>(() => PasswordAuthenticator.Respond("pw", challenge.Salt, [], HostFp, ControllerFp));
        Assert.Throws<ArgumentException>(
            () => PasswordAuthenticator.Respond("pw", challenge.Salt, new byte[PasswordAuthenticator.MaxPublicValueSize + 1], HostFp, ControllerFp));
        Assert.Throws<ArgumentException>(
            () => PasswordAuthenticator.Respond("pw", challenge.Salt.AsSpan(0, PasswordAuthenticator.SaltSize - 1), challenge.ServerPublic, HostFp, ControllerFp));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_client_refuses_a_server_public_value_of_zero_or_a_multiple_of_the_modulus(int multiple)
    {
        // B mod N == 0 is rejected by the SRP library with a CryptoException; ControllerSessionService turns that into
        // a protocol error instead of letting it escape.
        var challenge = new HostCredentials().BeginAuthentication(HostFp, ControllerFp)[0];
        var value = multiple == 0
            ? new byte[] { 0 }
            : GroupModulus().Multiply(Org.BouncyCastle.Math.BigInteger.ValueOf(multiple)).ToByteArrayUnsigned();

        Assert.Throws<Org.BouncyCastle.Crypto.CryptoException>(
            () => PasswordAuthenticator.Respond("pw", challenge.Salt, value, HostFp, ControllerFp));
    }
}

public class AuthAttemptLimiterTests
{
    private readonly TestClock _time = new();

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

    [Fact]
    public void The_table_stays_bounded_and_a_locked_out_device_keeps_its_lock_under_identity_rotation()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var locked = new DeviceId(111_111_111);
        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice; i++)
        {
            limiter.RecordFailure(locked);
        }

        // Far more one-failure identities than the table holds: the ones nobody has locked out are the ones dropped.
        for (var i = 0; i < AuthAttemptLimiter.MaxTrackedDevices * 2; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            limiter.RecordFailure(new DeviceId(400_000_000 + i));
        }

        Assert.True(limiter.IsBlocked(locked));
        Assert.False(limiter.IsBlocked(new DeviceId(999_999_999)));
    }

    [Fact]
    public void Expired_entries_are_dropped_without_waiting_for_the_table_to_fill()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);
        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice; i++)
        {
            limiter.RecordFailure(device);
        }

        _time.Advance(AuthAttemptLimiter.DeviceWindow + TimeSpan.FromMinutes(2));
        limiter.RecordFailure(new DeviceId(222_222_222));

        // Its window is over, so a single new failure starts a fresh count rather than meeting the old five.
        limiter.RecordFailure(device);
        Assert.False(limiter.IsBlocked(device));
    }

    [Fact]
    public void Unknown_devices_are_refused_while_every_slot_is_a_locked_out_device_and_released_when_they_age_out()
    {
        var limiter = new AuthAttemptLimiter(_time);
        for (var d = 0; d < AuthAttemptLimiter.MaxTrackedDevices; d++)
        {
            for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice; i++)
            {
                limiter.RecordFailure(new DeviceId(500_000_000 + d));
            }
        }

        var newcomer = new DeviceId(999_999_999);
        limiter.RecordFailure(newcomer);

        // Forgetting the newcomer's failure would hand it unlimited guesses, so it is refused instead.
        Assert.True(limiter.IsBlocked(newcomer));

        _time.Advance(AuthAttemptLimiter.DeviceWindow + TimeSpan.FromMinutes(2));
        Assert.False(limiter.IsBlocked(newcomer));
    }

    [Fact]
    public async Task Concurrent_handshakes_from_one_device_cannot_exceed_the_failure_budget()
    {
        // Every handshake is parked before the password is shown, like 64 simultaneous connections that have all passed
        // the old "is it blocked yet" check. Only the budget's worth may be let in; the rest are turned away at once.
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);
        using var start = new ManualResetEventSlim();

        var attempts = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return limiter.TryReserve(device);
        })).ToList();
        start.Set();
        var reservations = await Task.WhenAll(attempts);

        Assert.Equal(AuthAttemptLimiter.MaxFailuresPerDevice, reservations.Count(r => r is not null));
        Assert.Equal(AuthAttemptLimiter.MaxFailuresPerDevice, limiter.PendingReservations);

        // The ones that got in all end without evidence: nothing is counted, the budget is whole again.
        foreach (var reservation in reservations)
        {
            reservation?.Dispose();
        }

        Assert.Equal(0, limiter.PendingReservations);
        Assert.False(limiter.IsBlocked(device));
        Assert.NotNull(limiter.TryReserve(device));
    }

    [Fact]
    public void A_reservation_counts_together_with_recorded_failures()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);
        limiter.RecordFailure(device);
        limiter.RecordFailure(device);
        limiter.RecordFailure(device);

        using var first = limiter.TryReserve(device);
        using var second = limiter.TryReserve(device);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(limiter.TryReserve(device));
        Assert.False(limiter.IsBlocked(device));
        Assert.NotNull(limiter.TryReserve(new DeviceId(222_222_222)));
    }

    [Fact]
    public void A_successful_handshake_gives_its_reservation_back_and_clears_the_failures()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);
        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice - 1; i++)
        {
            limiter.RecordFailure(device);
        }

        var reservation = limiter.TryReserve(device);
        Assert.NotNull(reservation);
        reservation.RecordSuccess();

        Assert.Equal(0, limiter.PendingReservations);
        var again = Enumerable.Range(0, AuthAttemptLimiter.MaxFailuresPerDevice).Select(_ => limiter.TryReserve(device)).ToList();
        Assert.All(again, r => Assert.NotNull(r));
        Assert.Null(limiter.TryReserve(device));
    }

    [Fact]
    public void A_success_leaves_the_other_handshakes_of_the_same_device_alone()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);
        limiter.RecordFailure(device);
        limiter.RecordFailure(device);
        var winner = limiter.TryReserve(device);
        var other = limiter.TryReserve(device);
        Assert.NotNull(winner);
        Assert.NotNull(other);

        winner.RecordSuccess();

        // The failures are gone, the other handshake is still in flight and still holds one place.
        Assert.Equal(1, limiter.PendingReservations);
        var more = Enumerable.Range(0, AuthAttemptLimiter.MaxFailuresPerDevice - 1).Select(_ => limiter.TryReserve(device)).ToList();
        Assert.All(more, r => Assert.NotNull(r));
        Assert.Null(limiter.TryReserve(device));
    }

    [Fact]
    public void A_wrong_password_turns_the_reservation_into_a_failure_and_the_fifth_locks_the_device()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);

        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice; i++)
        {
            var reservation = limiter.TryReserve(device);
            Assert.NotNull(reservation);
            reservation.RecordFailure();
        }

        Assert.Equal(0, limiter.PendingReservations);
        Assert.True(limiter.IsBlocked(device));
        Assert.Null(limiter.TryReserve(device));

        _time.Advance(AuthAttemptLimiter.DeviceWindow + TimeSpan.FromSeconds(1));
        Assert.False(limiter.IsBlocked(device));
        Assert.NotNull(limiter.TryReserve(device));
    }

    [Fact]
    public void A_handshake_that_ends_before_any_evidence_counts_nothing_however_often_it_happens()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);

        for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice * 4; i++)
        {
            var reservation = limiter.TryReserve(device);
            Assert.NotNull(reservation);
            reservation.Dispose();
            reservation.Dispose();

            // Settling twice must not give back (or take) anything twice.
            reservation.RecordFailure();
        }

        Assert.Equal(0, limiter.PendingReservations);
        Assert.False(limiter.IsBlocked(device));
        Assert.All(
            Enumerable.Range(0, AuthAttemptLimiter.MaxFailuresPerDevice).Select(_ => limiter.TryReserve(device)).ToList(),
            r => Assert.NotNull(r));
    }

    [Fact]
    public void A_reservation_does_not_expire_with_the_window_and_is_not_swept_away()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);
        var held = Enumerable.Range(0, AuthAttemptLimiter.MaxFailuresPerDevice).Select(_ => limiter.TryReserve(device)).ToList();

        // A sweep runs (another device fails after the window and the sweep interval), and still the handshakes in
        // flight are counted: they end by their outcome, not by the clock.
        _time.Advance(AuthAttemptLimiter.DeviceWindow + TimeSpan.FromMinutes(2));
        limiter.RecordFailure(new DeviceId(222_222_222));

        Assert.Null(limiter.TryReserve(device));
        Assert.Equal(AuthAttemptLimiter.MaxFailuresPerDevice, limiter.PendingReservations);

        held[0]!.Dispose();
        Assert.NotNull(limiter.TryReserve(device));
    }

    [Fact]
    public void A_device_with_a_handshake_in_flight_is_not_evicted_when_the_table_makes_room()
    {
        var limiter = new AuthAttemptLimiter(_time);
        var device = new DeviceId(111_111_111);
        var held = limiter.TryReserve(device);
        Assert.NotNull(held);

        // The oldest entry by far, and the first candidate for eviction if the pending count did not pin it.
        for (var i = 0; i < AuthAttemptLimiter.MaxTrackedDevices + 10; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            limiter.RecordFailure(new DeviceId(600_000_000 + i));
        }

        var rest = Enumerable.Range(0, AuthAttemptLimiter.MaxFailuresPerDevice - 1).Select(_ => limiter.TryReserve(device)).ToList();
        Assert.All(rest, r => Assert.NotNull(r));
        Assert.Null(limiter.TryReserve(device));
        Assert.Equal(AuthAttemptLimiter.MaxFailuresPerDevice, limiter.PendingReservations);
    }

    [Fact]
    public void A_new_device_can_reserve_in_a_full_table_by_evicting_unlocked_devices()
    {
        var limiter = new AuthAttemptLimiter(_time);
        for (var d = 0; d < AuthAttemptLimiter.MaxTrackedDevices; d++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            limiter.RecordFailure(new DeviceId(700_000_000 + d));
        }

        using var reservation = limiter.TryReserve(new DeviceId(999_999_999));

        Assert.NotNull(reservation);
    }

    [Fact]
    public void Unknown_devices_cannot_reserve_while_every_slot_is_a_locked_out_device_and_can_once_they_age_out()
    {
        var limiter = new AuthAttemptLimiter(_time);
        for (var d = 0; d < AuthAttemptLimiter.MaxTrackedDevices; d++)
        {
            for (var i = 0; i < AuthAttemptLimiter.MaxFailuresPerDevice; i++)
            {
                limiter.RecordFailure(new DeviceId(500_000_000 + d));
            }
        }

        var newcomer = new DeviceId(999_999_999);
        Assert.Null(limiter.TryReserve(newcomer));
        Assert.Null(limiter.TryReserve(new DeviceId(500_000_000)));

        _time.Advance(AuthAttemptLimiter.DeviceWindow + TimeSpan.FromMinutes(2));
        Assert.NotNull(limiter.TryReserve(newcomer));
    }
}
