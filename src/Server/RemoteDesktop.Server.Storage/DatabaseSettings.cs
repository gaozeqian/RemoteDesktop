namespace RemoteDesktop.Server.Storage;

/// <summary>
/// Where device ids are kept. Bound from the <c>Rendezvous:Database</c> section; the two keys earlier versions used
/// are still read, so an existing deployment keeps working without touching its settings.
/// </summary>
/// <remarks>
/// <see cref="Provider"/> is null when the section does not say — that is what tells <see cref="Resolve"/> to fall
/// back to the legacy keys. A default of <c>none</c> would look the same as an explicit <c>none</c> and would quietly
/// stop reading them.
/// </remarks>
public sealed class DatabaseSettings
{
    public const string SectionName = "Database";

    /// <summary>Ids live in memory only. The default when nothing is configured.</summary>
    public const string None = "none";

    public const string Postgres = "postgres";

    public const string Sqlite = "sqlite";

    /// <summary><c>none</c>, <c>postgres</c> or <c>sqlite</c>. Null means "not stated".</summary>
    public string? Provider { get; set; }

    /// <summary>PostgreSQL connection string. Unused by the other providers.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The SQLite database file. Its folder is created if it is not there.</summary>
    public string? Path { get; set; }

    /// <summary>
    /// Fills in the provider the settings did not name.
    /// </summary>
    /// <param name="configured">What the settings section said.</param>
    /// <param name="legacyConnectionString">
    /// <c>Rendezvous:PostgresConnectionString</c> or <c>ConnectionStrings:Postgres</c>, whichever the deployment has.
    /// A non-empty one means PostgreSQL: that is what those keys meant when they were the only way to say it.
    /// </param>
    /// <remarks>
    /// Reading a legacy key is not deprecated behaviour being kept alive for its own sake — it is the difference
    /// between an upgrade that keeps every device code and one that hands out new ones. The cost is that the fallback
    /// has to stay until those keys are gone from every installer, which is a separate change.
    /// </remarks>
    public static DatabaseSettings Resolve(DatabaseSettings configured, string? legacyConnectionString)
    {
        ArgumentNullException.ThrowIfNull(configured);

        if (!string.IsNullOrWhiteSpace(configured.Provider))
        {
            return new DatabaseSettings
            {
                Provider = Canonical(configured.Provider),
                ConnectionString = Clean(configured.ConnectionString),
                Path = Clean(configured.Path),
            };
        }

        var legacy = Clean(legacyConnectionString);
        return legacy is null
            ? new DatabaseSettings { Provider = None }
            : new DatabaseSettings { Provider = Postgres, ConnectionString = legacy };
    }

    /// <summary>
    /// Refuses settings that cannot work, with a sentence naming the key. Called at start-up so a misconfigured
    /// deployment stops there instead of on the first device that connects.
    /// </summary>
    public void Validate()
    {
        switch (Provider)
        {
            case None:
                return;

            case Postgres:
                if (string.IsNullOrWhiteSpace(ConnectionString))
                {
                    throw new InvalidOperationException(
                        "Rendezvous:Database:ConnectionString is required when Rendezvous:Database:Provider is postgres (for example Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=...).");
                }

                return;

            case Sqlite:
                if (string.IsNullOrWhiteSpace(Path))
                {
                    throw new InvalidOperationException(
                        "Rendezvous:Database:Path is required when Rendezvous:Database:Provider is sqlite — the file the device ids are kept in, for example /var/lib/remotedesktop/rendezvous.db.");
                }

                return;

            default:
                throw new InvalidOperationException(
                    $"Rendezvous:Database:Provider must be one of {None}, {Postgres} or {Sqlite} (got '{Provider ?? "(empty)"}').");
        }
    }

    /// <summary>The constant for a configured name, matched without regard to case; anything else is refused.</summary>
    private static string Canonical(string provider)
    {
        var trimmed = provider.Trim();
        if (string.Equals(trimmed, None, StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        if (string.Equals(trimmed, Postgres, StringComparison.OrdinalIgnoreCase))
        {
            return Postgres;
        }

        if (string.Equals(trimmed, Sqlite, StringComparison.OrdinalIgnoreCase))
        {
            return Sqlite;
        }

        throw new InvalidOperationException(
            $"Rendezvous:Database:Provider must be one of {None}, {Postgres} or {Sqlite} (got '{provider}').");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
