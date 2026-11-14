using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace RemoteDesktop.Server.Shared;

public static partial class ServerSetup
{
    /// <summary>
    /// Writes where device codes are kept, and says so in the notes.
    /// </summary>
    /// <remarks>
    /// Nothing said means nothing changed, and that distinction is the whole point. The installers pass only what
    /// somebody filled in, and this is also the step used to fix an address or rotate a certificate months later.
    /// Reading "not given" as "clear it" would drop the connection string — and with it every device code — on an
    /// installation that only meant to change something else.
    /// </remarks>
    private static DatabaseChoice? ConfigureDatabase(JsonObject section, ServerSetupOptions options, List<string> notes)
    {
        var provider = Given(options.DatabaseProvider);

        // %ProgramData% is expanded here, the same way RelayPublicEndpoint and the certificate paths are. The server
        // reads Rendezvous:Database:Path as written, so an unexpanded value would create a folder literally named
        // %ProgramData% under the service's working directory.
        var path = Given(options.DatabasePath);
        if (path is not null)
        {
            path = Environment.ExpandEnvironmentVariables(path);
        }

        var postgres = Given(options.PostgresConnectionString);

        if (provider is null && path is null && postgres is null)
        {
            return null;
        }

        var (chosen, connectionString, databasePath) = Decide(provider, path, postgres, ReadDatabase(section));
        var database = Section(section, DatabaseSectionName);
        database["Provider"] = chosen;

        switch (chosen)
        {
            case ProviderNone:
                database.Remove("ConnectionString");
                database.Remove("Path");
                section.Remove(LegacyPostgresKey);
                notes.Add("Device codes are kept in memory only: a restart gives every device a new code. Give this server a database if machines are meant to be reachable with nobody present.");
                break;

            case ProviderPostgres:
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    // Writing a provider with no string leaves a server that cannot start, and the installer would
                    // report success because restarting a failed unit is not an error it looks at.
                    throw new InvalidOperationException(
                        "PostgreSQL was chosen but no connection string was given. Pass one (for example Host=127.0.0.1;Port=5432;Database=remotedesktop;Username=remotedesktop;Password=...) or choose SQLite.");
                }

                database["ConnectionString"] = connectionString;
                database.Remove("Path");

                // An older server reads this key and nothing else, and it means the same thing, so it is written too.
                section[LegacyPostgresKey] = connectionString;
                notes.Add("Device codes are kept in PostgreSQL.");
                break;

            default:
                database["Path"] = databasePath;
                database.Remove("ConnectionString");
                section.Remove(LegacyPostgresKey);
                notes.Add($"Device codes are kept in SQLite at {databasePath}. Back that file up — losing it gives every device a new code.");
                break;
        }

        return new DatabaseChoice(chosen, connectionString, databasePath);
    }

    /// <summary>What the settings file already says about the database, which is what <c>auto</c> decides from.</summary>
    private static ExistingDatabase ReadDatabase(JsonObject section)
    {
        var database = section[DatabaseSectionName] as JsonObject;
        return new ExistingDatabase(
            Text(database, "Provider"),
            Text(database, "ConnectionString"),
            Text(database, "Path"),
            Text(section, LegacyPostgresKey));
    }

    private static (string Provider, string? ConnectionString, string? Path) Decide(
        string? provider,
        string? path,
        string? postgres,
        ExistingDatabase existing)
    {
        if (provider is not null)
        {
            if (IsAuto(provider))
            {
                // What the caller gave this run wins over what the machine had before: an installer that filled the
                // box in said what it wanted, and "auto" only means "and otherwise decide for this machine".
                if (postgres is { Length: > 0 } && !IsAuto(postgres))
                {
                    return (ProviderPostgres, postgres, null);
                }

                return path is { Length: > 0 }
                    ? (ProviderSqlite, null, path)
                    : Automatic(existing);
            }

            if (string.Equals(provider, ProviderNone, StringComparison.OrdinalIgnoreCase))
            {
                return (ProviderNone, null, null);
            }

            if (string.Equals(provider, ProviderPostgres, StringComparison.OrdinalIgnoreCase))
            {
                return (ProviderPostgres, FirstGiven(postgres, existing.ConnectionString, existing.LegacyConnectionString), null);
            }

            if (string.Equals(provider, ProviderSqlite, StringComparison.OrdinalIgnoreCase))
            {
                return (ProviderSqlite, null, FirstGiven(path, existing.Path) ?? DefaultSqlitePath());
            }

            throw new InvalidOperationException($"The database provider must be auto, none, postgres or sqlite (got '{provider}').");
        }

        if (postgres is not null)
        {
            // An empty connection string is how the installers have always said "no database"; "auto" is the value
            // the package ships when nobody filled the box in, and means "decide for this machine".
            return postgres.Length == 0
                ? (ProviderNone, null, null)
                : IsAuto(postgres) ? Automatic(existing) : (ProviderPostgres, postgres, null);
        }

        // A file with no provider can only mean the database that has one. An empty path names none, and writing it
        // would only fail later, when the service starts and rejects the setting.
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException("The SQLite database path is empty. Pass a file path, or choose a provider with --database.")
            : (ProviderSqlite, null, path);
    }

    /// <summary>
    /// What <c>auto</c> resolves to on this machine: keep what is already configured, and otherwise start a SQLite
    /// database. An existing PostgreSQL connection string wins because it is the one case where switching would
    /// reissue every device code on the installation.
    /// </summary>
    private static (string Provider, string? ConnectionString, string? Path) Automatic(ExistingDatabase existing)
    {
        // "auto" means "keep what this machine already uses". "none" is a choice too — the memory-only mode an
        // operator picked on purpose — and turning it into a SQLite file on an upgrade silently changes the behaviour
        // the operator asked for.
        if (string.Equals(existing.Provider, ProviderNone, StringComparison.OrdinalIgnoreCase))
        {
            return (ProviderNone, null, null);
        }

        if (string.Equals(existing.Provider, ProviderSqlite, StringComparison.OrdinalIgnoreCase))
        {
            return (ProviderSqlite, null, existing.Path ?? DefaultSqlitePath());
        }

        var connectionString = FirstGiven(existing.ConnectionString, existing.LegacyConnectionString);
        if (string.Equals(existing.Provider, ProviderPostgres, StringComparison.OrdinalIgnoreCase))
        {
            return connectionString is null
                ? throw new InvalidOperationException("The database provider is postgres but there is no connection string to use. Pass one, or pass 'none' to keep device codes in memory.")
                : (ProviderPostgres, connectionString, null);
        }

        return connectionString is not null
            ? (ProviderPostgres, connectionString, null)
            : (ProviderSqlite, null, existing.Path ?? DefaultSqlitePath());
    }

    /// <summary>
    /// Where a SQLite database goes when the installer did not name a file: the platform's own place for a service's
    /// data, not the install directory. An upgrade replaces what is in the install directory, and this file is the
    /// only thing standing between an unattended machine and a new device code.
    /// </summary>
    private static string DefaultSqlitePath() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktopServer", "rendezvous.db")
            : OperatingSystem.IsMacOS()
                ? "/usr/local/remotedesktop-server/var/rendezvous.db"
                : "/var/lib/remotedesktop-server/rendezvous.db";

    /// <summary>The provider and the values the provider needs, as they were written.</summary>
    private readonly record struct DatabaseChoice(string Provider, string? ConnectionString, string? Path);

    /// <summary>
    /// What the settings file said before this run. The provider is null when the file predates it, which is what
    /// tells <see cref="Automatic"/> to look at the connection string instead.
    /// </summary>
    private readonly record struct ExistingDatabase(
        string? Provider,
        string? ConnectionString,
        string? Path,
        string? LegacyConnectionString);

}
