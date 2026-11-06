using System.Data.Common;
using System.Globalization;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>One console account, as stored. <see cref="PasswordHash"/> and <see cref="TotpSecret"/> never leave the host.</summary>
public sealed record OpsAdminUser(
    string Id,
    string Username,
    string PasswordHash,
    string TotpSecret,
    string Role,
    bool Disabled,
    long CreatedAt);

/// <summary>One account as the management page reads it: no password hash and no TOTP secret.</summary>
public sealed record OpsAdminUserSummary(string Id, string Username, string Role, bool Disabled, long CreatedAt);

public sealed partial class OpsStore
{
    private const string UserColumns = "id, username, password_hash, totp_secret, role, disabled, created_at";

    /// <summary>
    /// The lookup key for a user name. Upper-cased rather than lower-cased so the value is stable under a collation
    /// change and matches what the unique index compares; the display name keeps the case it was typed in.
    /// </summary>
    public static string UsernameKey(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        return username.Trim().ToUpperInvariant();
    }

    /// <summary>The account named <paramref name="username"/>, or <c>null</c>. Case and surrounding space do not matter.</summary>
    public async Task<OpsAdminUser?> FindUserAsync(string username, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {UserColumns} FROM ops_admin_user WHERE username_key = @key;";
        Bind(command, ("@key", UsernameKey(username)));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
    }

    /// <summary>The account with this id, or <c>null</c>. Used by the TOTP step, which knows only a challenge's user id.</summary>
    public async Task<OpsAdminUser?> FindUserByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {UserColumns} FROM ops_admin_user WHERE id = @id;";
        Bind(command, ("@id", userId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
    }

    /// <summary>Every account, by name, without either secret.</summary>
    public async Task<IReadOnlyList<OpsAdminUserSummary>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, username, role, disabled, created_at FROM ops_admin_user ORDER BY username;";

        var users = new List<OpsAdminUserSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            users.Add(new OpsAdminUserSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                reader.GetInt64(4)));
        }

        return users;
    }

    /// <summary>How many administrators can still sign in. The guard against locking the console out.</summary>
    public async Task<long> CountEnabledAdminsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ops_admin_user WHERE role = @role AND disabled = 0;";
        Bind(command, ("@role", OpsRoles.Admin));

        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Replaces an account's password and ends every session it had. A password change that leaves old cookies alive
    /// is not a password change.
    /// </summary>
    public async Task<bool> UpdateUserPasswordAsync(string id, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);

        return await UpdateUserAndRevokeSessionsAsync(
            id,
            "UPDATE ops_admin_user SET password_hash = @value WHERE id = @id;",
            ("@value", passwordHash),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces an account's TOTP secret and ends every session it had, so the next sign-in needs the new code.</summary>
    public async Task<bool> UpdateUserTotpSecretAsync(string id, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        return await UpdateUserAndRevokeSessionsAsync(
            id,
            "UPDATE ops_admin_user SET totp_secret = @value, last_totp_step = NULL WHERE id = @id;",
            ("@value", secret),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disables or enables an account. Disabling ends its sessions. The last enabled administrator cannot be
    /// disabled: that is the one change that would leave nobody able to sign in.
    /// </summary>
    public async Task<bool> SetUserDisabledAsync(string id, bool disabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE ops_admin_user SET disabled = @disabled
                WHERE id = @id
                  AND (@disabled = 0 OR role <> @role
                       OR (SELECT COUNT(*) FROM ops_admin_user u WHERE u.role = @role AND u.disabled = 0) > 1);
                """;
            Bind(command, ("@id", id), ("@disabled", disabled ? 1 : 0), ("@role", OpsRoles.Admin));

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        if (disabled)
        {
            await DeleteUserSessionsAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes an account and its sessions. The last enabled administrator cannot be deleted, for the same reason it
    /// cannot be disabled.
    /// </summary>
    public async Task<bool> DeleteUserAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM ops_admin_user
                WHERE id = @id
                  AND (role <> @role OR disabled = 1
                       OR (SELECT COUNT(*) FROM ops_admin_user u WHERE u.role = @role AND u.disabled = 0) > 1);
                """;
            Bind(command, ("@id", id), ("@role", OpsRoles.Admin));

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await DeleteUserSessionsAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Records that <paramref name="step"/> was just used for a TOTP code. False means the code was already used (or
    /// an older one), which is the replay this column exists to refuse. The comparison and the write are one
    /// statement, so two simultaneous sign-ins cannot both consume the same step.
    /// </summary>
    public async Task<bool> TryConsumeTotpStepAsync(string userId, long step, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ops_admin_user SET last_totp_step = @step
            WHERE id = @id AND (last_totp_step IS NULL OR last_totp_step < @step);
            """;
        Bind(command, ("@id", userId), ("@step", step));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Inserts an account. A duplicate name surfaces as the provider's unique-constraint error.</summary>
    public async Task CreateUserAsync(OpsAdminUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_admin_user (id, username, username_key, password_hash, totp_secret, role, disabled, created_at)
            VALUES (@id, @username, @key, @passwordHash, @totpSecret, @role, @disabled, @createdAt);
            """;
        Bind(
            command,
            ("@id", user.Id),
            ("@username", user.Username),
            ("@key", UsernameKey(user.Username)),
            ("@passwordHash", user.PasswordHash),
            ("@totpSecret", user.TotpSecret),
            ("@role", user.Role),
            ("@disabled", user.Disabled ? 1 : 0),
            ("@createdAt", user.CreatedAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates one column and drops the account's sessions in the same transaction.</summary>
    private async Task<bool> UpdateUserAndRevokeSessionsAsync(
        string id,
        string sql,
        (string Name, object? Value) value,
        CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            Bind(command, ("@id", id), value);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await DeleteUserSessionsAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task DeleteUserSessionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ops_admin_session WHERE user_id = @userId;";
        Bind(command, ("@userId", userId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OpsAdminUser ReadUser(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5) != 0,
            reader.GetInt64(6));
}
