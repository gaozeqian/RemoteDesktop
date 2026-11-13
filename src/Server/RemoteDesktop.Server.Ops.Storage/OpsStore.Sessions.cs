using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>One signed-in browser. <see cref="TokenHash"/> is the SHA-256 of the cookie, never the cookie.</summary>
public sealed record OpsSessionRecord(
    string Id,
    string UserId,
    string TokenHash,
    long IssuedAt,
    long ExpiresAt,
    string? Ip,
    string? UserAgent);

public sealed partial class OpsStore
{
    /// <summary>Stores a session and returns nothing — the caller already holds the token.</summary>
    public async Task CreateSessionAsync(OpsSessionRecord session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_admin_session (id, user_id, token_hash, issued_at, expires_at, ip, user_agent)
            VALUES (@id, @userId, @tokenHash, @issuedAt, @expiresAt, @ip, @userAgent);
            """;
        Bind(
            command,
            ("@id", session.Id),
            ("@userId", session.UserId),
            ("@tokenHash", session.TokenHash),
            ("@issuedAt", session.IssuedAt),
            ("@expiresAt", session.ExpiresAt),
            ("@ip", session.Ip),
            ("@userAgent", session.UserAgent));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The account behind a live session cookie, or <c>null</c> when the session is expired, unknown or the account is
    /// disabled. One query: the authentication path runs on every request and should not cost two round trips.
    /// </summary>
    public async Task<OpsAdminUser?> FindUserBySessionAsync(
        string tokenHash,
        long nowUnixMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenHash);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.id, u.username, u.password_hash, u.totp_secret, u.role, u.disabled, u.created_at
            FROM ops_admin_session s
            JOIN ops_admin_user u ON u.id = s.user_id
            WHERE s.token_hash = @tokenHash AND s.expires_at > @now AND u.disabled = 0;
            """;
        Bind(command, ("@tokenHash", tokenHash), ("@now", nowUnixMilliseconds));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
    }

    /// <summary>Deletes the session with this cookie hash if it exists. Used by sign-out.</summary>
    public async Task RevokeSessionAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenHash);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ops_admin_session WHERE token_hash = @tokenHash;";
        Bind(command, ("@tokenHash", tokenHash));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops expired sessions. Called by the maintenance task; returns how many rows went.</summary>
    public async Task<int> DeleteExpiredSessionsAsync(long nowUnixMilliseconds, CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ops_admin_session WHERE expires_at <= @now;";
        Bind(command, ("@now", nowUnixMilliseconds));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes projected sessions whose last activity is before the cutoff, in bounded batches. A session that never
    /// ended is included when its projection stopped being updated: a connection that has been silent for the whole
    /// retention window is not going to report an end.
    /// </summary>
    public async Task<int> DeleteSessionsBeforeAsync(
        long beforeUnixMilliseconds,
        int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM ops_session WHERE id IN (
                SELECT id FROM ops_session
                WHERE ended_at < @before OR (ended_at IS NULL AND updated_at < @before)
                LIMIT @limit
            );
            """;
        Bind(command, ("@before", beforeUnixMilliseconds), ("@limit", Math.Clamp(limit, 1, 20_000)));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
