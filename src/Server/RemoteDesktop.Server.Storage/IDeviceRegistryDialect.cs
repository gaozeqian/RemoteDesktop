namespace RemoteDesktop.Server.Storage;

/// <summary>
/// The part of the device registry that is different in every database: four statements, how a unique violation is
/// recognised, and whether the store can take more than one writer at a time.
/// </summary>
/// <remarks>
/// Everything else — the cache, the nine-digit id, the collision retry, the distinction between a new device and a
/// reconnecting one — lives once in <see cref="RelationalDeviceRegistry"/>. That split is what makes a fourth
/// database a file rather than a copy: the parts that are easy to get subtly wrong are not the parts that differ.
///
/// The statements are shaped so that one implementation can serve providers that disagree on everything else. The
/// insert is <c>INSERT … ON CONFLICT (fingerprint) DO NOTHING</c> and the number of rows it reports is what says
/// whether this call created the id — not <c>RETURNING</c>, which MySQL does not have. Timestamps are parameters
/// rather than <c>NOW()</c>, so no dialect has to spell "the current instant". The probe is a plain
/// <c>SELECT</c> on the table, so a database whose file has been deleted fails instead of answering.
/// </remarks>
internal interface IDeviceRegistryDialect
{
    /// <summary>Creates the table and its index when they are absent. May hold several statements.</summary>
    string CreateSchemaSql { get; }

    /// <summary>Returns the id stored for <c>@fingerprint</c>, or no row.</summary>
    string SelectDeviceIdSql { get; }

    /// <summary>
    /// Inserts unless the fingerprint is already there. Affected rows are 1 when the row was inserted and 0 when it
    /// was not — that difference is how the caller knows whether it created the device.
    /// </summary>
    string InsertIfAbsentSql { get; }

    /// <summary>Refreshes <c>last_seen_at</c> for <c>@fingerprint</c>.</summary>
    string TouchLastSeenSql { get; }

    /// <summary>Adds <c>revoked_at</c> to a table that predates it. Run after <see cref="CreateSchemaSql"/> on every start.</summary>
    string AddRevokedAtSql { get; }

    /// <summary>Sets or clears <c>revoked_at</c> for <c>@device_id</c>. <c>@revoked</c> is the timestamp or NULL.</summary>
    string SetRevokedSql { get; }

    /// <summary>Returns one row when the device exists: <c>1</c> when revoked, <c>0</c> when not.</summary>
    string IsRevokedSql { get; }

    /// <summary>Whether <paramref name="exception"/> is only "the column already exists", so the migration can be idempotent.</summary>
    bool IsDuplicateColumn(Exception exception);

    /// <summary>
    /// Whether a write must be serialised in-process. True where the database has a single writer (SQLite); false
    /// where concurrent writers are its normal mode of operation and a lock would only add a queue.
    /// </summary>
    bool SerializeWrites { get; }

    /// <summary>Whether <paramref name="exception"/> is a unique-constraint violation on the device id column.</summary>
    bool IsUniqueViolation(Exception exception);
}
