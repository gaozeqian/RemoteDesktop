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
    /// Points the monitoring console at a database. It shares the provider with the signaling server, because an
    /// operator who chose PostgreSQL meant the installation, not one of its processes.
    /// </summary>
    /// <returns><c>true</c> when the console's settings were changed and have to be written.</returns>
    private static bool ConfigureOps(
        JsonObject settings,
        DatabaseChoice? database,
        ServerSetupOptions options,
        List<string> notes)
    {
        var listen = Given(options.OpsListen);
        var databasePath = Given(options.OpsDatabasePath);
        if (databasePath is not null)
        {
            databasePath = Environment.ExpandEnvironmentVariables(databasePath);
        }

        var nodeCertificatePath = Given(options.OpsNodeCertificatePath);
        if (database is null && listen is null && databasePath is null && nodeCertificatePath is null)
        {
            return false;
        }

        var section = Section(settings, "Ops");
        if (listen is not null)
        {
            section["Listen"] = listen;
        }

        if (nodeCertificatePath is not null)
        {
            // The console pins this certificate when it calls its nodes. Empty clears a pin left by a previous TLS
            // deployment.
            if (nodeCertificatePath.Length > 0)
            {
                section["NodeCertificatePath"] = nodeCertificatePath;
            }
            else
            {
                section.Remove("NodeCertificatePath");
            }
        }

        if (database is not { } choice)
        {
            if (databasePath is null)
            {
                return true;
            }

            // Only a console path was given. That is still a SQLite console: it has no other provider to fall back to.
            var sqlite = Section(section, DatabaseSectionName);
            sqlite["Provider"] = ProviderSqlite;
            sqlite["Path"] = databasePath;
            sqlite.Remove("ConnectionString");
            return true;
        }

        var console = Section(section, DatabaseSectionName);
        if (choice.Provider == ProviderPostgres)
        {
            console["Provider"] = ProviderPostgres;
            console["ConnectionString"] = choice.ConnectionString;
            console.Remove("Path");
            notes.Add("The monitoring console uses the same PostgreSQL database.");
        }
        else
        {
            // "none" is a legitimate choice for the signaling server (memory only) but not for the console: a console
            // with nowhere to write cannot answer a single question, so it always gets a SQLite file.
            var path = databasePath ?? DefaultOpsSqlitePath();
            console["Provider"] = ProviderSqlite;
            console["Path"] = path;
            console.Remove("ConnectionString");
            notes.Add($"The monitoring console keeps its data in SQLite at {path}.");
        }

        return true;
    }

    /// <summary>Where the console's SQLite file goes when the console was not given one.</summary>
    private static string DefaultOpsSqlitePath() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesktopServer", "ops.db")
            : OperatingSystem.IsMacOS()
                ? "/usr/local/remotedesktop-server/var/ops.db"
                : "/var/lib/remotedesktop-server/ops.db";

}
