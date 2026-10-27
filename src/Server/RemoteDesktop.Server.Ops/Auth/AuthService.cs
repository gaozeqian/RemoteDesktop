using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.Auth;

/// <summary>How far a sign-in got. The endpoint maps each to one status code; nothing else interprets them.</summary>
public enum SignInStatus
{
    /// <summary>Password accepted; a TOTP code is still needed.</summary>
    ChallengeRequired,

    /// <summary>Fully signed in; <see cref="SignInOutcome.Session"/> is set.</summary>
    Succeeded,

    /// <summary>Wrong name or password. Deliberately the same answer for both.</summary>
    InvalidCredentials,

    /// <summary>Too many failures; see <see cref="SignInOutcome.RetryAfter"/>.</summary>
    Locked,

    /// <summary>The account exists and the password was right, but the account is disabled.</summary>
    Disabled,
}

/// <summary>The outcome of one sign-in step.</summary>
public sealed record SignInOutcome(
    SignInStatus Status,
    string? Challenge = null,
    DateTimeOffset? ExpiresAt = null,
    TimeSpan? RetryAfter = null,
    AuthSession? Session = null);

/// <summary>A signed-in browser's secrets and account.</summary>
public sealed record AuthSession(string Token, string Csrf, OpsAdminUser User);

/// <summary>
/// Sign-in, sign-out and the audit records around them. Kept out of the endpoint handlers so the same sequence can be
/// driven by a test without an HTTP request.
/// </summary>
public sealed class AuthService(
    OpsStore store,
    OpsOptions options,
    LoginThrottle throttle,
    AddressThrottle addressThrottle,
    ChallengeStore challenges,
    TimeProvider time)
{
    /// <summary>
    /// A hash of a password nobody knows, verified when the account does not exist so a wrong name and a wrong password
    /// take the same time. Computed once: the PBKDF2 work is the point, not the value.
    /// </summary>
    private static readonly string DecoyHash = PasswordHasher.Hash("decoy-" + Guid.NewGuid().ToString("N"));

    /// <summary>Password step. On success either starts a TOTP challenge or signs the browser in.</summary>
    public async Task<SignInOutcome> SignInAsync(
        string? username,
        string? password,
        string? address,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var key = LoginThrottle.KeyFor(address, username);
        if (throttle.IsLocked(key, out var retryAfter))
        {
            return new SignInOutcome(SignInStatus.Locked, RetryAfter: retryAfter);
        }

        // A spray across user names never spends one name's budget; this one counts every failure from the address.
        if (addressThrottle.IsLocked(address, out var addressRetryAfter))
        {
            return new SignInOutcome(SignInStatus.Locked, RetryAfter: addressRetryAfter);
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throttle.RecordFailure(key);
            addressThrottle.RecordFailure(address);
            return new SignInOutcome(SignInStatus.InvalidCredentials);
        }

        var user = await store.FindUserAsync(username, cancellationToken).ConfigureAwait(false);
        var matches = PasswordHasher.Verify(password, user?.PasswordHash ?? DecoyHash);

        if (user is null || !matches)
        {
            throttle.RecordFailure(key);
            addressThrottle.RecordFailure(address);
            if (user is not null)
            {
                await AuditAsync(user.Username, "auth.login.denied", user.Id, address, "denied", cancellationToken)
                    .ConfigureAwait(false);
            }

            return new SignInOutcome(SignInStatus.InvalidCredentials);
        }

        if (user.Disabled)
        {
            await AuditAsync(user.Username, "auth.login.disabled", user.Id, address, "denied", cancellationToken)
                .ConfigureAwait(false);
            return new SignInOutcome(SignInStatus.Disabled);
        }

        throttle.Reset(key);

        if (!options.RequireTotp)
        {
            return new SignInOutcome(SignInStatus.Succeeded, Session: await CreateSessionAsync(user, address, userAgent, cancellationToken).ConfigureAwait(false));
        }

        var challenge = challenges.Create(user.Id, out var expiresAt);
        return new SignInOutcome(SignInStatus.ChallengeRequired, challenge, expiresAt);
    }

    /// <summary>TOTP step: exchanges a challenge and a code for a session.</summary>
    public async Task<SignInOutcome> CompleteTotpAsync(
        string? challengeId,
        string? code,
        string? address,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (!challenges.TryGet(challengeId, out var userId, out _))
        {
            return new SignInOutcome(SignInStatus.InvalidCredentials);
        }

        var user = await store.FindUserByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Disabled)
        {
            challenges.Remove(challengeId!);
            return new SignInOutcome(SignInStatus.InvalidCredentials);
        }

        // A second budget, separate from the password one. The password step resets its key on a correct password —
        // which is exactly what an attacker who already knows the password would do to get a fresh set of guesses
        // after starting a new challenge. This key survives both new challenges and a correct password, so the
        // effort needed to brute-force the six digits does not scale with how many challenges one can mint.
        var totpKey = TotpKeyFor(address, user.Username);
        if (throttle.IsLocked(totpKey, out var retryAfter))
        {
            return new SignInOutcome(SignInStatus.Locked, RetryAfter: retryAfter);
        }

        if (addressThrottle.IsLocked(address, out var addressRetryAfter))
        {
            return new SignInOutcome(SignInStatus.Locked, RetryAfter: addressRetryAfter);
        }

        if (!Totp.TryVerify(user.TotpSecret, code ?? string.Empty, time.GetUtcNow(), out var step))
        {
            throttle.RecordFailure(totpKey);
            addressThrottle.RecordFailure(address);

            if (!challenges.RecordFailure(challengeId!))
            {
                await AuditAsync(user.Username, "auth.totp.locked", user.Id, address, "denied", cancellationToken)
                    .ConfigureAwait(false);
            }

            return new SignInOutcome(SignInStatus.InvalidCredentials);
        }

        // A valid code is still only valid once. Without this, anyone who sees the six digits — over a shoulder, in a
        // screenshot, in a proxy log — can present them again with their own password challenge inside the
        // acceptance window. The update is atomic, so two simultaneous requests cannot both consume the same step.
        if (!await store.TryConsumeTotpStepAsync(user.Id, step, cancellationToken).ConfigureAwait(false))
        {
            throttle.RecordFailure(totpKey);
            addressThrottle.RecordFailure(address);
            await AuditAsync(user.Username, "auth.totp.replay", user.Id, address, "denied", cancellationToken)
                .ConfigureAwait(false);
            return new SignInOutcome(SignInStatus.InvalidCredentials);
        }

        throttle.Reset(totpKey);
        challenges.Remove(challengeId!);
        return new SignInOutcome(SignInStatus.Succeeded, Session: await CreateSessionAsync(user, address, userAgent, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Revokes the session behind <paramref name="token"/> and records that it happened.</summary>
    public async Task SignOutAsync(string? token, string? actor, string? address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        await store.RevokeSessionAsync(SessionToken.Hash(token), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(actor))
        {
            await AuditAsync(actor, "auth.logout", "-", address, "success", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AuthSession> CreateSessionAsync(
        OpsAdminUser user,
        string? address,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var token = SessionToken.Create();
        var csrf = SessionToken.Create();

        await store.CreateSessionAsync(
            new OpsSessionRecord(
                Guid.NewGuid().ToString("N"),
                user.Id,
                SessionToken.Hash(token),
                now.ToUnixTimeMilliseconds(),
                now.Add(options.SessionLifetime).ToUnixTimeMilliseconds(),
                address,
                userAgent),
            cancellationToken).ConfigureAwait(false);

        await AuditAsync(user.Username, "auth.login", user.Id, address, "success", cancellationToken).ConfigureAwait(false);
        return new AuthSession(token, csrf, user);
    }

    /// <summary>
    /// The failure budget for the second factor, kept apart from the password's by a prefix in the same table.
    /// </summary>
    private static string TotpKeyFor(string? address, string username) =>
        string.Concat("totp\n", LoginThrottle.KeyFor(address, username));

    private Task<string> AuditAsync(string actor, string action, string target, string? address, string result, CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                time.GetUtcNow().ToUnixTimeMilliseconds(),
                actor,
                action,
                target,
                "{}",
                address ?? "-",
                result),
            cancellationToken);
}
