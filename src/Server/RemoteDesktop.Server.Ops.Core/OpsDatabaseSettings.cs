namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// Where the console keeps its own data. Same three shapes the device registry accepts, minus <c>none</c>: a console
/// with nowhere to write cannot answer a single question, so "in memory" is not one of its options.
/// </summary>
/// <remarks>
/// Deliberately a second settings type rather than a reuse of <c>RemoteDesktop.Server.Storage.DatabaseSettings</c>.
/// That one resolves the legacy <c>ConnectionStrings:Postgres</c> keys and names <c>Rendezvous:Database</c> in its
/// error messages — history the console does not have. Sharing the type would either drag that history along or force
/// a change to a running service's configuration semantics.
/// </remarks>
public sealed class OpsDatabaseSettings
{
    public const string SectionName = "Database";

    public const string Sqlite = "sqlite";

    public const string Postgres = "postgres";

    /// <summary><c>sqlite</c> or <c>postgres</c>.</summary>
    public string Provider { get; set; } = Sqlite;

    /// <summary>The SQLite file. Its folder is created if it is not there.</summary>
    public string? Path { get; set; }

    /// <summary>PostgreSQL connection string. Unused by the other provider.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Refuses settings that cannot work, naming the key that has to change.</summary>
    public void Validate()
    {
        var provider = Canonical(Provider)
            ?? throw new InvalidOperationException(
                $"Ops:Database:Provider must be {Sqlite} or {Postgres} (got '{Provider ?? "(empty)"}').");

        if (string.Equals(provider, Sqlite, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(Path))
            {
                throw new InvalidOperationException(
                    "Ops:Database:Path is required when Ops:Database:Provider is sqlite — for example /var/lib/remotedesktop/ops.db.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                "Ops:Database:ConnectionString is required when Ops:Database:Provider is postgres (for example Host=db;Port=5432;Database=remotedesktop_ops;Username=rd;Password=...).");
        }
    }

    /// <summary>
    /// Resolves the SQLite file the same way for the service and the setup commands.
    /// </summary>
    /// <remarks>
    /// Environment variables are expanded first because the packaged Windows settings say <c>%ProgramData%</c>. A
    /// relative path is then resolved against one explicit base directory, never against the process's current
    /// directory: a Windows service starts in System32, and a CLI run from somewhere else must still find the file the
    /// service will open. An expansion that produces nothing is treated as "not configured" so the caller's
    /// <see cref="Validate"/> names the key rather than opening the configuration directory as a database.
    /// </remarks>
    public static string? ResolvePath(string? provider, string? path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        if (!string.Equals(Canonical(provider), Sqlite, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path).Trim();
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return null;
        }

        return System.IO.Path.IsPathRooted(expanded)
            ? expanded
            : System.IO.Path.Combine(baseDirectory, expanded);
    }

    /// <summary>The stored spelling of a provider name, or <c>null</c> when it is neither of the two.</summary>
    public static string? Canonical(string? provider)
    {
        if (string.Equals(provider?.Trim(), Sqlite, StringComparison.OrdinalIgnoreCase))
        {
            return Sqlite;
        }

        return string.Equals(provider?.Trim(), Postgres, StringComparison.OrdinalIgnoreCase) ? Postgres : null;
    }
}
