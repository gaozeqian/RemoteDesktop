namespace RemoteDesktop.Server.Ops.Storage;

public sealed partial class OpsStore
{
    /// <summary>
    /// Takes or renews a named lease, true when this holder now owns it.
    /// </summary>
    /// <remarks>
    /// The console's background tasks — scrape, alert evaluation, retention — must not run twice. A single deployment
    /// never contends, so the lease is invisible there; a second replica loses it and stops working instead of sending
    /// every notification twice. That failure is silent without this table, which is why it exists before it is needed.
    /// </remarks>
    public async Task<bool> TryAcquireLeaseAsync(
        string name,
        string holder,
        long nowUnixMilliseconds,
        long expiresAtUnixMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_lease (name, holder, expires_at) VALUES (@name, @holder, @expiresAt)
            ON CONFLICT (name) DO UPDATE SET holder = excluded.holder, expires_at = excluded.expires_at
            WHERE ops_lease.expires_at < @now OR ops_lease.holder = @holder;
            """;
        Bind(
            command,
            ("@name", name),
            ("@holder", holder),
            ("@now", nowUnixMilliseconds),
            ("@expiresAt", expiresAtUnixMilliseconds));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
