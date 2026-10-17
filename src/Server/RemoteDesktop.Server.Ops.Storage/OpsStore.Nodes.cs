using System.Data.Common;
using System.Globalization;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>A node the console scrapes. Its display name is the console's own; the node's <c>/internal/v1/info</c>
/// name is informational, because renaming must not require touching the server.</summary>
public sealed record OpsNode(
    string Id,
    string Name,
    string Role,
    string BaseUrl,
    string? Region,
    bool Enabled,
    long? LastScrapeAt,
    bool? LastScrapeOk,
    long CreatedAt);

public sealed partial class OpsStore
{
    private const string NodeColumns = "id, name, role, base_url, region, enabled, last_scrape_at, last_scrape_ok, created_at";

    /// <summary>Every node, enabled or not, by name. The console shows disabled ones so an operator can see what was
    /// switched off rather than wonder where it went.</summary>
    public async Task<IReadOnlyList<OpsNode>> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {NodeColumns} FROM ops_node ORDER BY name;";

        var nodes = new List<OpsNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(ReadNode(reader));
        }

        return nodes;
    }

    /// <summary>Adds a node. A duplicate name surfaces as the provider's unique-constraint error.</summary>
    public async Task CreateNodeAsync(OpsNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_node (id, name, role, base_url, region, enabled, last_scrape_at, last_scrape_ok, created_at)
            VALUES (@id, @name, @role, @baseUrl, @region, @enabled, NULL, NULL, @createdAt);
            """;
        Bind(
            command,
            ("@id", node.Id),
            ("@name", node.Name),
            ("@role", node.Role),
            ("@baseUrl", node.BaseUrl),
            ("@region", node.Region),
            ("@enabled", node.Enabled ? 1 : 0),
            ("@createdAt", node.CreatedAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enables or disables a node. Returns false when the id is unknown.</summary>
    public async Task<bool> SetNodeEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ops_node SET enabled = @enabled WHERE id = @id;";
        Bind(command, ("@id", id), ("@enabled", enabled ? 1 : 0));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Removes a node. Its samples go with it; history for a node nobody watches is a cost with no reader.</summary>
    public async Task<bool> DeleteNodeAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var samples = connection.CreateCommand())
        {
            samples.Transaction = transaction;
            samples.CommandText = "DELETE FROM ops_metric_sample WHERE node_id = @id;";
            Bind(samples, ("@id", id));
            await samples.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var node = connection.CreateCommand())
        {
            node.Transaction = transaction;
            node.CommandText = "DELETE FROM ops_node WHERE id = @id;";
            Bind(node, ("@id", id));
            var removed = await node.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return removed > 0;
        }
    }

    /// <summary>Records how the last scrape went, which is what the node card's status is drawn from.</summary>
    public async Task RecordScrapeAsync(string id, long timestamp, bool ok, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ops_node SET last_scrape_at = @ts, last_scrape_ok = @ok WHERE id = @id;";
        Bind(command, ("@id", id), ("@ts", timestamp), ("@ok", ok ? 1 : 0));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Number of registered nodes, for the shell's empty state.</summary>
    public async Task<long> CountNodesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ops_node;";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    private static OpsNode ReadNode(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5) != 0,
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7) != 0,
            reader.GetInt64(8));
}
