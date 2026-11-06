using System.Globalization;
using System.Text.Json;

namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>
/// A window in which notifications for a rule and/or a node are not sent. The alert itself still opens and resolves —
/// the silence is about the noise, not about the history.
/// </summary>
/// <param name="Id">Stable id, so a page can delete exactly the row it showed.</param>
/// <param name="RuleId">The rule this covers, or <c>null</c> for "every rule".</param>
/// <param name="Node">The node name this covers (scrape rules only), or <c>null</c> for "every node".</param>
/// <param name="Reason">Why it was silenced; shown to whoever finds the gap in their inbox.</param>
/// <param name="CreatedBy">The administrator who created it.</param>
/// <param name="CreatedAt">Unix milliseconds.</param>
/// <param name="UntilAt">Unix milliseconds; the row stops matching at this instant.</param>
public sealed record OpsSilenceRow(
    string Id,
    string? RuleId,
    string? Node,
    string Reason,
    string CreatedBy,
    long CreatedAt,
    long UntilAt);

public sealed partial class OpsStore
{
    // The table was designed with a matcher blob rather than columns, so the scope lives here. Camel case because a
    // person may end up reading it with sqlite3 while debugging a silence that does not do what they expected.
    private static readonly JsonSerializerOptions MatcherJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Silences that still apply: those whose window has not closed yet. Expired rows are ignored, not deleted.</summary>
    public async Task<IReadOnlyList<OpsSilenceRow>> ListActiveSilencesAsync(long nowUnixMilliseconds, CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, matcher_json, starts_at, ends_at, created_by, reason
            FROM ops_silence
            WHERE ends_at > @now AND starts_at <= @now
            ORDER BY ends_at;
            """;
        Bind(command, ("@now", nowUnixMilliseconds));

        var rows = new List<OpsSilenceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ReadSilence(reader) is { } row)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>Adds a silence window.</summary>
    public async Task CreateSilenceAsync(OpsSilenceRow silence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(silence);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ops_silence (id, matcher_json, starts_at, ends_at, created_by, reason)
            VALUES (@id, @matcher, @startsAt, @endsAt, @createdBy, @reason);
            """;
        Bind(
            command,
            ("@id", silence.Id),
            ("@matcher", JsonSerializer.Serialize(new Matcher(silence.RuleId, silence.Node), MatcherJson)),
            ("@startsAt", silence.CreatedAt),
            ("@endsAt", silence.UntilAt),
            ("@createdBy", silence.CreatedBy),
            ("@reason", silence.Reason));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds one silence, so the API can delete it and audit what it was.</summary>
    public async Task<OpsSilenceRow?> FindSilenceAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, matcher_json, starts_at, ends_at, created_by, reason
            FROM ops_silence
            WHERE id = @id;
            """;
        Bind(command, ("@id", id));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSilence(reader) : null;
    }

    /// <summary>Removes a silence. Ending one early is a delete: the window is the row.</summary>
    public async Task<bool> DeleteSilenceAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ops_silence WHERE id = @id;";
        Bind(command, ("@id", id));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Reads one row, or <c>null</c> when its matcher is not valid JSON. A row nobody can interpret must not become a
    /// silence that covers everything, so it is skipped instead.
    /// </summary>
    private static OpsSilenceRow? ReadSilence(System.Data.Common.DbDataReader reader)
    {
        Matcher? matcher;
        try
        {
            matcher = JsonSerializer.Deserialize<Matcher>(reader.GetString(1), MatcherJson);
        }
        catch (JsonException)
        {
            return null;
        }

        return new OpsSilenceRow(
            reader.GetString(0),
            matcher?.RuleId,
            matcher?.Node,
            reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            reader.GetString(4),
            Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture));
    }

    private sealed record Matcher(string? RuleId, string? Node);
}
