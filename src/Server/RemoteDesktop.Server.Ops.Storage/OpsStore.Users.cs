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

    /// <summary>True when the console has at least one account — the first-run check for <c>--create-admin</c>.</summary>
    public async Task<bool> HasAnyUserAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ops_admin_user;";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
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
