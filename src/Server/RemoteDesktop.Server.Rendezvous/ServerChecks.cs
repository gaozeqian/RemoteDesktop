using System.Globalization;
using System.Net;
using Npgsql;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Answers "is this value usable?" for the two settings whose typos are otherwise only discovered by a server that
/// will not start: the PostgreSQL connection string, and the address clients are told to reach the relay on.
/// </summary>
/// <remarks>
/// These are checks, not gates. A database that is not up yet is a normal thing to install against — the settings are
/// written either way, and the answer is shown to whoever is at the screen so they can decide.
/// </remarks>
public static class ServerChecks
{
    /// <summary>How long the database gets. Long enough for a real server, short enough not to hold up an install.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether PostgreSQL answers this connection string.</summary>
    public static (bool Ok, string Message) Database(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "No database is configured: device codes will live in memory and change when the signaling server restarts.");
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                // Bounded here rather than trusting whatever is in the string: this runs while somebody waits.
                Timeout = (int)Timeout.TotalSeconds,
                CommandTimeout = (int)Timeout.TotalSeconds,
            };

            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();

            // Not just "can I reach it": the servers keep their device registry in a schema of their own, and a
            // database the account may not write to fails later in a way that is much harder to read.
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_database(), version()";
            using var reader = command.ExecuteReader();
            var name = reader.Read() ? reader.GetString(0) : "?";

            return (true, $"Connected to PostgreSQL database '{name}'.");
        }
        catch (Exception ex) when (ex is NpgsqlException or ArgumentException or InvalidOperationException or FormatException)
        {
            return (false, $"Could not connect: {ex.Message}");
        }
    }

    /// <summary>Whether a relay address is one a client could be handed: an address and a port, or a name that resolves to an address.</summary>
    public static (bool Ok, string Message) Relay(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "No relay address was given: clients will be told to use the first address of this machine that is not loopback. Fill one in when the relay runs elsewhere, or when this machine has a VPN adapter.");
        }

        var parts = address.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is <= 0 or > ushort.MaxValue)
        {
            return (false, "A relay address is 'host:port' (the client sends UDP straight to it), for example 203.0.113.10:5082.");
        }

        var host = parts[0];
        if (IPAddress.TryParse(host, out var parsed))
        {
            // Reachability cannot be checked here: the relay only answers a rendezvous-signed ticket, which an
            // installer has no way to obtain. What can be checked is that it is an address at all.
            return (true, $"Clients will be told to use {parsed}:{port}.");
        }

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            var v4 = addresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray();
            if (v4.Length == 0)
            {
                return (false, $"'{host}' resolves, but not to an IPv4 address, and clients are handed an IP:port.");
            }

            return (true, $"'{host}' resolves to {string.Join(", ", v4.Select(a => a.ToString()))}:{port}. The name is resolved once, now — the settings will hold this address.");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return (false, $"'{host}' does not resolve: {ex.Message}");
        }
    }
}
