using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Storage.UnitTests;

/// <summary>
/// The device registry against a real PostgreSQL server: the shared contract, plus what only a server can get wrong —
/// parameter types, <c>TIMESTAMPTZ</c> round trips, <c>ON CONFLICT</c> under concurrency and the idempotent schema.
/// Skipped unless <c>RD_TEST_POSTGRES</c> is set; see CONTRIBUTING.md.
/// </summary>
public sealed class PostgresDeviceRegistryTests : DeviceRegistryContractTests, IAsyncLifetime
{
    private static readonly ILogger<PostgresDeviceRegistry> Logger = NullLogger<PostgresDeviceRegistry>.Instance;

    private readonly List<PostgresTestSchema> _schemas = [];
    private readonly List<PostgresDeviceRegistry> _registries = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var registry in _registries)
        {
            registry.Dispose();
        }

        foreach (var schema in _schemas)
        {
            await schema.DisposeAsync();
        }
    }

    protected override IDeviceRegistry CreateEmptyRegistry() => NewRegistry(NewSchema());

    [Fact]
    public async Task Ids_survive_a_new_registry_instance()
    {
        var schema = NewSchema();

        var first = NewRegistry(schema);
        await first.EnsureSchemaAsync(Ct);
        var allocated = (await first.GetOrAllocateAsync("fingerprint-A", Ct)).DeviceId;

        var second = NewRegistry(schema);
        var again = await second.GetOrAllocateAsync("fingerprint-A", Ct);

        Assert.Equal(allocated, again.DeviceId);
        Assert.False(again.Created);
    }

    /// <summary>
    /// Two server processes (or one process after a restart) draw ids independently and share only the table. Exactly
    /// one insert may win; the others must read the winner's id back instead of failing or minting a second one.
    /// </summary>
    [Fact]
    public async Task Registries_with_separate_caches_agree_on_one_fingerprint()
    {
        var schema = NewSchema();
        var registries = Enumerable.Range(0, 4).Select(_ => NewRegistry(schema)).ToArray();
        await registries[0].EnsureSchemaAsync(Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(index => registries[index % registries.Length].GetOrAllocateAsync("fingerprint-shared", Ct)));

        Assert.Single(results.Select(result => result.DeviceId).Distinct());
        Assert.Single(results, result => result.Created);
        Assert.Single(await registries[0].ListAsync(0, 10, Ct));
    }

    /// <summary>
    /// A start-up that finds the table, with or without the column added later, must succeed every time: the host runs
    /// this on each start and a failure stops it.
    /// </summary>
    [Fact]
    public async Task Ensuring_the_schema_is_repeatable_and_adds_the_revocation_column_to_an_old_table()
    {
        var schema = NewSchema();
        await schema.ExecuteAsync(
            """
            CREATE TABLE device_registrations (
                fingerprint VARCHAR(128) PRIMARY KEY,
                device_id BIGINT UNIQUE NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            INSERT INTO device_registrations (fingerprint, device_id) VALUES ('fingerprint-old', 123456789);
            """,
            Ct);

        var registry = NewRegistry(schema);
        await registry.EnsureSchemaAsync(Ct);
        await registry.EnsureSchemaAsync(Ct);

        var known = await registry.GetOrAllocateAsync("fingerprint-old", Ct);
        Assert.Equal(123_456_789u, known.DeviceId);
        Assert.False(known.Created);

        await registry.SetRevokedAsync(known.DeviceId, revoked: true, Ct);
        Assert.True(await registry.IsRevokedAsync(known.DeviceId, Ct));
    }

    /// <summary>
    /// Several server processes starting together (a rolling restart behind a load balancer) all create the table.
    /// <c>IF NOT EXISTS</c> alone is not safe against a concurrent creator on PostgreSQL.
    /// </summary>
    [Fact]
    public async Task Registries_starting_at_the_same_time_all_ensure_the_schema()
    {
        var schema = NewSchema();
        var registries = Enumerable.Range(0, 6).Select(_ => NewRegistry(schema)).ToArray();

        await Task.WhenAll(registries.Select(registry => registry.EnsureSchemaAsync(Ct)));

        await registries[0].ProbeAsync(Ct);
    }

    [Fact]
    public async Task Revocation_round_trips_and_appears_in_the_list()
    {
        var schema = NewSchema();
        var registry = NewRegistry(schema);
        await registry.EnsureSchemaAsync(Ct);
        var deviceId = (await registry.GetOrAllocateAsync("fingerprint-A", Ct)).DeviceId;

        Assert.False(await registry.IsRevokedAsync(deviceId, Ct));
        Assert.False(await registry.IsRevokedAsync(deviceId + 1, Ct), "an unknown device is not revoked");

        await registry.SetRevokedAsync(deviceId, revoked: true, Ct);
        Assert.True(await registry.IsRevokedAsync(deviceId, Ct));
        Assert.True(Assert.Single(await NewRegistry(schema).ListAsync(0, 10, Ct)).Revoked);

        await registry.SetRevokedAsync(deviceId, revoked: false, Ct);
        Assert.False(await registry.IsRevokedAsync(deviceId, Ct));
        Assert.False(Assert.Single(await registry.ListAsync(0, 10, Ct)).Revoked);
    }

    /// <summary>The catalog's cursor is exclusive and ordered by id; TIMESTAMPTZ values come back as Unix milliseconds.</summary>
    [Fact]
    public async Task The_catalog_pages_by_id_and_reports_timestamps_in_unix_milliseconds()
    {
        var registry = NewRegistry(NewSchema());
        await registry.EnsureSchemaAsync(Ct);

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ids = new List<uint>();
        for (var index = 0; index < 5; index++)
        {
            ids.Add((await registry.GetOrAllocateAsync($"fingerprint-{index}", Ct)).DeviceId);
        }

        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ids.Sort();

        var first = await registry.ListAsync(0, 2, Ct);
        Assert.Equal([ids[0], ids[1]], first.Select(record => record.DeviceId));
        var second = await registry.ListAsync(first[^1].DeviceId, 2, Ct);
        Assert.Equal([ids[2], ids[3]], second.Select(record => record.DeviceId));
        var third = await registry.ListAsync(second[^1].DeviceId, 2, Ct);
        Assert.Equal([ids[4]], third.Select(record => record.DeviceId));

        // PostgreSQL keeps microseconds and the registry reports milliseconds, so the stored instant must land inside
        // the window in which the rows were created: no timezone shift, no rounding past the end.
        foreach (var record in first.Concat(second).Concat(third))
        {
            Assert.InRange(record.FirstSeenUnixMilliseconds, before, after);
            Assert.InRange(record.LastSeenUnixMilliseconds, before, after);
        }
    }

    /// <summary>
    /// A registry that has just started has an empty cache, so the first registration of a known device goes through
    /// the select and then the touch — the path that writes <c>last_seen_at</c> as a <c>TIMESTAMPTZ</c> parameter.
    /// </summary>
    [Fact]
    public async Task A_known_device_that_registers_again_refreshes_last_seen()
    {
        var schema = NewSchema();
        var seed = NewRegistry(schema);
        await seed.EnsureSchemaAsync(Ct);
        var deviceId = (await seed.GetOrAllocateAsync("fingerprint-A", Ct)).DeviceId;

        await schema.ExecuteAsync("UPDATE device_registrations SET last_seen_at = NOW() - INTERVAL '1 day';", Ct);
        var stale = Assert.Single(await seed.ListAsync(0, 10, Ct)).LastSeenUnixMilliseconds;

        var restarted = NewRegistry(schema);
        Assert.Equal(deviceId, (await restarted.GetOrAllocateAsync("fingerprint-A", Ct)).DeviceId);

        // The touch is fire-and-forget, so wait for it rather than assume it has landed.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        long current;
        do
        {
            current = Assert.Single(await seed.ListAsync(0, 10, Ct)).LastSeenUnixMilliseconds;
            if (current > stale)
            {
                break;
            }

            await Task.Delay(25, Ct);
        }
        while (DateTime.UtcNow < deadline);

        Assert.True(current - stale > TimeSpan.FromHours(23).TotalMilliseconds, "last_seen_at should have moved to now");
    }

    [Fact]
    public async Task The_probe_succeeds_with_the_table_and_fails_without_it()
    {
        var schema = NewSchema();
        var registry = NewRegistry(schema);
        await registry.EnsureSchemaAsync(Ct);
        await registry.ProbeAsync(Ct);

        await schema.ExecuteAsync("DROP TABLE device_registrations;", Ct);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => registry.ProbeAsync(Ct));
        Assert.Equal(PostgresErrorCodes.UndefinedTable, failure.SqlState);
    }

    /// <summary>
    /// The collision retry depends on the dialect recognising a duplicate device id as a unique violation, while a
    /// duplicate fingerprint is absorbed by <c>ON CONFLICT</c> and never raises at all.
    /// </summary>
    [Fact]
    public async Task A_duplicate_device_id_is_a_unique_violation_the_dialect_recognises()
    {
        var schema = NewSchema();
        var registry = NewRegistry(schema);
        await registry.EnsureSchemaAsync(Ct);
        await schema.ExecuteAsync("INSERT INTO device_registrations (fingerprint, device_id) VALUES ('a', 111111111);", Ct);

        var duplicateId = await Assert.ThrowsAsync<PostgresException>(() =>
            schema.ExecuteAsync("INSERT INTO device_registrations (fingerprint, device_id) VALUES ('b', 111111111);", Ct));
        Assert.True(new PostgresDeviceRegistry.PostgresDialect().IsUniqueViolation(duplicateId));

        var duplicateFingerprint = new PostgresDeviceRegistry.PostgresDialect().InsertIfAbsentSql;
        await using var connection = await schema.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = duplicateFingerprint;
        command.Parameters.AddWithValue("fingerprint", "a");
        command.Parameters.AddWithValue("device_id", 222_222_222L);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        Assert.Equal(0, await command.ExecuteNonQueryAsync(Ct));
    }

    private PostgresTestSchema NewSchema()
    {
        var schema = PostgresTestSchema.Create();
        _schemas.Add(schema);
        return schema;
    }

    private PostgresDeviceRegistry NewRegistry(PostgresTestSchema schema)
    {
        var registry = new PostgresDeviceRegistry(schema.ConnectionString, Logger);
        _registries.Add(registry);
        return registry;
    }
}
