using System.Globalization;
using System.Text.Json;
using Npgsql;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// <see cref="OpsStore"/> against a real PostgreSQL server. The store is written in the intersection of two SQL
/// dialects, and only a server can say whether the PostgreSQL half of that intersection really holds: parameter
/// types, <c>ON CONFLICT</c>, window functions, the advisory lock, transactional DDL, <c>DOUBLE PRECISION</c>.
/// </summary>
/// <remarks>
/// Opt-in: every test is skipped unless <c>RD_TEST_POSTGRES</c> holds an Npgsql connection string (see
/// CONTRIBUTING.md). Each test gets its own schema, created and dropped around it. The assertions deliberately mirror
/// the SQLite ones in <c>StorageMaintenanceTests</c> and <c>SchemaMigrationTests</c>; they are copied rather than
/// shared so that the SQLite tests stay as they are.
/// </remarks>
public sealed class PostgresOpsStoreTests : IAsyncLifetime
{
    private readonly List<PostgresDatabase> _databases = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await database.DisposeAsync();
        }
    }

    // ---- schema and migrations ------------------------------------------------------------------------------

    [Fact]
    public async Task A_fresh_database_gets_every_table_and_every_migration_recorded()
    {
        var db = await NewDatabaseAsync();

        Assert.Equal(OpsDatabaseSettings.Postgres, db.Store.Provider);
        Assert.Equal(
            OpsMigrations.All.Select(migration => (long)migration.Version).Order(),
            await db.ColumnAsync<long>("SELECT version FROM ops_schema_version ORDER BY version;"));

        var tables = await db.ColumnAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema();");
        string[] expected =
        [
            "ops_schema_version", "ops_node", "ops_metric_catalog", "ops_metric_sample", "ops_metric_rollup", "ops_event",
            "ops_session", "ops_alert_rule", "ops_alert", "ops_notification_channel", "ops_notification_attempt",
            "ops_silence", "ops_admin_user", "ops_admin_session", "ops_audit_log", "ops_lease", "ops_admin_action",
        ];
        Assert.Empty(expected.Except(tables));

        await db.Store.ProbeAsync(Ct);
    }

    /// <summary>A second start would fail on the first ADD COLUMN if the recorded version were not honoured.</summary>
    [Fact]
    public async Task Starting_twice_does_not_run_the_migrations_twice()
    {
        var db = await NewDatabaseAsync();
        var versions = await db.ColumnAsync<long>("SELECT version FROM ops_schema_version ORDER BY version;");

        using var second = OpsStore.Open(db.Settings);
        await second.EnsureSchemaAsync(Ct);
        await second.EnsureSchemaAsync(Ct);

        Assert.Equal(versions, await db.ColumnAsync<long>("SELECT version FROM ops_schema_version ORDER BY version;"));
    }

    /// <summary>
    /// Several console processes starting together (a rolling restart) all run the schema script. The script is
    /// <c>IF NOT EXISTS</c> throughout, which on PostgreSQL is not by itself safe against a concurrent creator.
    /// </summary>
    [Fact]
    public async Task Stores_starting_at_the_same_time_all_succeed()
    {
        var db = await NewDatabaseAsync(ensureSchema: false);
        var stores = Enumerable.Range(0, 4).Select(_ => OpsStore.Open(db.Settings)).ToArray();
        try
        {
            await Task.WhenAll(stores.Select(store => store.EnsureSchemaAsync(Ct)));
        }
        finally
        {
            foreach (var store in stores)
            {
                store.Dispose();
            }
        }

        Assert.Equal((long)OpsSchema.Version, (await db.ColumnAsync<long>("SELECT MAX(version) FROM ops_schema_version;")).Single());
    }

    /// <summary>
    /// The database a version-1 deployment left behind: the baseline with four-byte REAL columns and no version beyond
    /// 1. Starting this build must add what was added since, widen the REAL columns and keep the rows that were there.
    /// </summary>
    [Fact]
    public async Task A_version_one_database_is_upgraded_in_place_and_keeps_its_rows()
    {
        var db = await NewDatabaseAsync(ensureSchema: false);
        await db.ExecuteAsync(
            OpsSchema.Sql + """

            ALTER TABLE ops_metric_sample ALTER COLUMN value TYPE REAL;
            ALTER TABLE ops_metric_rollup ALTER COLUMN first_value TYPE REAL;
            ALTER TABLE ops_metric_rollup ALTER COLUMN last_value TYPE REAL;
            ALTER TABLE ops_metric_rollup ALTER COLUMN min_value TYPE REAL;
            ALTER TABLE ops_metric_rollup ALTER COLUMN max_value TYPE REAL;
            ALTER TABLE ops_metric_rollup ALTER COLUMN avg_value TYPE REAL;
            ALTER TABLE ops_session ALTER COLUMN rtt_ms TYPE REAL;
            ALTER TABLE ops_session ALTER COLUMN loss_pct TYPE REAL;
            ALTER TABLE ops_alert_rule ALTER COLUMN threshold TYPE REAL;
            ALTER TABLE ops_alert ALTER COLUMN value TYPE REAL;

            INSERT INTO ops_schema_version (version, applied_at) VALUES (1, 0);
            INSERT INTO ops_session (id, correlation_id, source, updated_at) VALUES ('s1', 'c1', 'relay', 1);
            INSERT INTO ops_node (id, name, role, base_url, created_at) VALUES ('n1', 'relay-1', 'relay', 'https://relay-1', 1);
            INSERT INTO ops_metric_sample (node_id, metric, ts, value) VALUES ('n1', 'm', 1, 0.5);
            """,
            Ct);

        await db.Store.EnsureSchemaAsync(Ct);

        Assert.Equal((long)OpsSchema.Version, (await db.ColumnAsync<long>("SELECT MAX(version) FROM ops_schema_version;")).Single());
        Assert.Equal(
            ["client_session_id", "platform", "version"],
            await db.ColumnAsync<string>(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() "
                + "AND table_name = 'ops_session' AND column_name IN ('platform', 'version', 'client_session_id') ORDER BY column_name;"));
        Assert.Equal(
            ["double precision"],
            await db.ColumnAsync<string>(
                "SELECT DISTINCT data_type FROM information_schema.columns WHERE table_schema = current_schema() "
                + "AND ((table_name = 'ops_metric_sample' AND column_name = 'value') "
                + "OR (table_name = 'ops_session' AND column_name IN ('rtt_ms', 'loss_pct')) "
                + "OR (table_name = 'ops_alert_rule' AND column_name = 'threshold'));"));

        var node = await db.Store.FindNodeAsync("n1", Ct);
        Assert.Equal("relay-1", node!.Name);
        Assert.Null(node.Token);
        Assert.Equal("relay", (await db.Store.FindSessionAsync("s1", Ct))!.Source);
        Assert.Equal(0.5, Assert.Single(await db.Store.ReadSeriesAsync(["m"], 0, long.MaxValue, Ct)).Value);

        // 2^24 + 1 is the first integer a four-byte float cannot hold; after the upgrade it must survive.
        await db.Store.WriteSamplesAsync("n1", 2, [new PrometheusSample("m", "m", 16_777_217)], Ct);
        Assert.Equal(16_777_217d, (await db.Store.ReadSeriesAsync(["m"], 2, 2, Ct)).Single().Value);
    }

    /// <summary>
    /// Each step runs in its own transaction. Version 2 adds two columns; with the second already present the step
    /// fails after the first statement has run, and nothing of it may remain.
    /// </summary>
    [Fact]
    public async Task A_migration_that_fails_part_way_leaves_nothing_behind()
    {
        var db = await NewDatabaseAsync(ensureSchema: false);
        await db.ExecuteAsync(
            OpsSchema.Sql + """

            INSERT INTO ops_schema_version (version, applied_at) VALUES (1, 0);
            ALTER TABLE ops_session ADD COLUMN version TEXT;
            """,
            Ct);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Store.EnsureSchemaAsync(Ct));

        Assert.Equal(PostgresErrorCodes.DuplicateColumn, failure.SqlState);
        Assert.Empty(await db.ColumnAsync<string>(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() "
            + "AND table_name = 'ops_session' AND column_name = 'platform';"));
        Assert.Equal([1L], await db.ColumnAsync<long>("SELECT version FROM ops_schema_version;"));
    }

    // ---- nodes, accounts and sign-in sessions ---------------------------------------------------------------

    [Fact]
    public async Task Nodes_can_be_created_read_changed_and_deleted_with_their_samples()
    {
        var db = await NewDatabaseAsync();
        var node = new OpsNode("n1", "relay-1", "relay", "https://relay-1", "eu", Enabled: true, null, null, 1_000, Token: "secret");
        await db.Store.CreateNodeAsync(node, Ct);

        Assert.Equal(node, await db.Store.FindNodeAsync("n1", Ct));
        Assert.Equal(1, await db.Store.CountNodesAsync(Ct));

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Store.CreateNodeAsync(node with { Id = "n2" }, Ct));
        Assert.True(OpsStore.IsUniqueViolation(duplicate), "a duplicate name must surface as a unique violation");

        await db.Store.RecordScrapeAsync("n1", 5_000, ok: true, Ct);
        var scraped = await db.Store.FindNodeAsync("n1", Ct);
        Assert.Equal(5_000, scraped!.LastScrapeAt);
        Assert.True(scraped.LastScrapeOk);

        // A new address makes the old scrape status meaningless; the enabled flag alone leaves it.
        Assert.True(await db.Store.UpdateNodeAsync("n1", enabled: false, baseUrl: null, Ct));
        var disabled = await db.Store.FindNodeAsync("n1", Ct);
        Assert.False(disabled!.Enabled);
        Assert.Equal(5_000, disabled.LastScrapeAt);

        Assert.True(await db.Store.UpdateNodeAsync("n1", enabled: true, baseUrl: "https://moved", Ct));
        var moved = await db.Store.FindNodeAsync("n1", Ct);
        Assert.True(moved!.Enabled);
        Assert.Equal("https://moved", moved.BaseUrl);
        Assert.Null(moved.LastScrapeAt);
        Assert.Null(moved.LastScrapeOk);

        Assert.True(await db.Store.SetNodeTokenAsync("n1", null, Ct));
        Assert.Null((await db.Store.FindNodeAsync("n1", Ct))!.Token);
        Assert.True(await db.Store.SetNodeEnabledAsync("n1", false, Ct));
        Assert.True(await db.Store.UpdateNodeBaseUrlAsync("relay-1", "https://by-name", Ct));
        Assert.False(await db.Store.UpdateNodeBaseUrlAsync("no-such-node", "https://x", Ct));
        Assert.Equal("relay-1", Assert.Single(await db.Store.ListNodesAsync(Ct)).Name);

        await db.Store.WriteSamplesAsync("n1", 1, [new PrometheusSample("m", "m", 1)], Ct);
        Assert.True(await db.Store.DeleteNodeAsync("n1", Ct));
        Assert.False(await db.Store.DeleteNodeAsync("n1", Ct));
        Assert.Empty(await db.Store.ReadSeriesAsync(["m"], 0, long.MaxValue, Ct));
    }

    [Fact]
    public async Task Accounts_are_found_case_insensitively_and_the_last_administrator_is_protected()
    {
        var db = await NewDatabaseAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var first = User("Alice", OpsRoles.Admin, now);
        var second = User("bob", OpsRoles.Admin, now);
        await db.Store.CreateUserAsync(first, Ct);
        await db.Store.CreateUserAsync(second, Ct);

        Assert.Equal(first, await db.Store.FindUserAsync("  ALICE ", Ct));
        Assert.Equal(second, await db.Store.FindUserByIdAsync(second.Id, Ct));
        Assert.Equal(["Alice", "bob"], (await db.Store.ListUsersAsync(Ct)).Select(user => user.Username));

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Store.CreateUserAsync(User("ALICE", OpsRoles.Admin, now), Ct));
        Assert.True(OpsStore.IsUniqueViolation(duplicate));

        Assert.Equal(2, await db.Store.CountEnabledAdminsAsync(Ct));
        Assert.True(await db.Store.SetUserDisabledAsync(first.Id, true, Ct));
        Assert.True((await db.Store.FindUserByIdAsync(first.Id, Ct))!.Disabled);
        Assert.False(await db.Store.SetUserDisabledAsync(second.Id, true, Ct));
        Assert.False(await db.Store.DeleteUserAsync(second.Id, Ct));
        Assert.Equal(1, await db.Store.CountEnabledAdminsAsync(Ct));

        Assert.True(await db.Store.SetUserDisabledAsync(first.Id, false, Ct));
        Assert.True(await db.Store.DeleteUserAsync(first.Id, Ct));
        Assert.False(await db.Store.DeleteUserAsync("missing", Ct));
    }

    [Fact]
    public async Task A_totp_step_can_be_consumed_once_and_only_forward()
    {
        var db = await NewDatabaseAsync();
        var user = User("alice", OpsRoles.Admin, 1);
        await db.Store.CreateUserAsync(user, Ct);

        Assert.True(await db.Store.TryConsumeTotpStepAsync(user.Id, 100, Ct));
        Assert.False(await db.Store.TryConsumeTotpStepAsync(user.Id, 100, Ct));
        Assert.False(await db.Store.TryConsumeTotpStepAsync(user.Id, 99, Ct));
        Assert.True(await db.Store.TryConsumeTotpStepAsync(user.Id, 101, Ct));

        // Two simultaneous sign-ins with the same code: exactly one may win.
        var race = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.Store.TryConsumeTotpStepAsync(user.Id, 500, Ct)));
        Assert.Single(race, won => won);

        // A new secret forgets the consumed step.
        Assert.True(await db.Store.UpdateUserTotpSecretAsync(user.Id, Totp.GenerateSecret(), Ct));
        Assert.True(await db.Store.TryConsumeTotpStepAsync(user.Id, 1, Ct));
    }

    [Fact]
    public async Task Sign_in_sessions_expire_and_end_with_the_account_changes_that_should_end_them()
    {
        var db = await NewDatabaseAsync();
        var user = User("alice", OpsRoles.Admin, 1);
        var other = User("bob", OpsRoles.Admin, 1);
        await db.Store.CreateUserAsync(user, Ct);
        await db.Store.CreateUserAsync(other, Ct);

        await db.Store.CreateSessionAsync(new OpsSessionRecord("s1", user.Id, "hash-1", 1_000, 10_000, "10.0.0.1", "agent"), Ct);
        await db.Store.CreateSessionAsync(new OpsSessionRecord("s2", user.Id, "hash-2", 1_000, 5_000, null, null), Ct);
        await db.Store.CreateSessionAsync(new OpsSessionRecord("s3", other.Id, "hash-3", 1_000, 10_000, null, null), Ct);

        Assert.Equal(user, await db.Store.FindUserBySessionAsync("hash-1", 9_999, Ct));
        Assert.Null(await db.Store.FindUserBySessionAsync("hash-1", 10_000, Ct));
        Assert.Null(await db.Store.FindUserBySessionAsync("unknown", 1_000, Ct));

        Assert.Equal(1, await db.Store.DeleteExpiredSessionsAsync(5_000, Ct));
        Assert.Null(await db.Store.FindUserBySessionAsync("hash-2", 1_000, Ct));

        await db.Store.RevokeSessionAsync("hash-3", Ct);
        Assert.Null(await db.Store.FindUserBySessionAsync("hash-3", 1_000, Ct));

        // Changing the password ends every session of that account, in the same transaction as the change.
        Assert.True(await db.Store.UpdateUserPasswordAsync(user.Id, PasswordHasher.Hash("a new passphrase"), Ct));
        Assert.Null(await db.Store.FindUserBySessionAsync("hash-1", 1_000, Ct));
        Assert.False(await db.Store.UpdateUserPasswordAsync("missing", PasswordHasher.Hash("x"), Ct));

        // A disabled account's live session stops authenticating.
        await db.Store.CreateSessionAsync(new OpsSessionRecord("s4", other.Id, "hash-4", 1_000, 10_000, null, null), Ct);
        Assert.True(await db.Store.SetUserDisabledAsync(user.Id, true, Ct));
        Assert.NotNull(await db.Store.FindUserBySessionAsync("hash-4", 1_000, Ct));
        Assert.True(await db.Store.SetUserDisabledAsync(user.Id, false, Ct));
        Assert.True(await db.Store.SetUserDisabledAsync(other.Id, true, Ct));
        Assert.Null(await db.Store.FindUserBySessionAsync("hash-4", 1_000, Ct));
    }

    // ---- audit chain, leases --------------------------------------------------------------------------------

    /// <summary>The advisory transaction lock is what keeps two stores from reading the same previous hash.</summary>
    [Fact]
    public async Task Two_store_instances_append_one_audit_chain()
    {
        var db = await NewDatabaseAsync();
        using var second = OpsStore.Open(db.Settings);

        var tasks = Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? db.Store : second).AppendAuditAsync(
            Audit(index.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            Ct));
        await Task.WhenAll(tasks);

        var records = await db.Store.ReadAuditChainAsync(Ct);
        Assert.Equal(20, records.Count);
        Assert.Equal(20, await db.Store.CountAuditEntriesAsync(Ct));
        Assert.True(AuditVerifier.Verify(records).IsValid, "the chain must not fork when two stores append concurrently");
    }

    /// <summary>
    /// Entries written with one captured "now" must still be strictly ordered, and the listing pages newest first by
    /// timestamp with an exclusive cursor.
    /// </summary>
    [Fact]
    public async Task Audit_entries_with_equal_timestamps_stay_ordered_and_page_newest_first()
    {
        var db = await NewDatabaseAsync();
        for (var index = 0; index < 5; index++)
        {
            await db.Store.AppendAuditAsync(Audit("target-" + index, 1_000_000), Ct);
        }

        var chain = await db.Store.ReadAuditChainAsync(Ct);
        Assert.Equal([1_000_000L, 1_000_001, 1_000_002, 1_000_003, 1_000_004], chain.Select(record => record.Timestamp));
        Assert.Equal(AuditChain.Genesis, chain[0].PrevHash);
        Assert.True(AuditVerifier.Verify(chain).IsValid);

        var firstPage = await db.Store.ListAuditAsync(2, null, Ct);
        Assert.Equal(["target-4", "target-3"], firstPage.Select(record => record.Target));
        var secondPage = await db.Store.ListAuditAsync(2, firstPage[^1].Timestamp, Ct);
        Assert.Equal(["target-2", "target-1"], secondPage.Select(record => record.Target));
    }

    [Fact]
    public async Task A_lease_belongs_to_one_holder_until_it_expires_or_is_released()
    {
        var db = await NewDatabaseAsync();

        Assert.True(await db.Store.TryAcquireLeaseAsync("scrape", "a", 1_000, 2_000, Ct));
        Assert.False(await db.Store.TryAcquireLeaseAsync("scrape", "b", 1_500, 2_500, Ct));
        Assert.True(await db.Store.TryAcquireLeaseAsync("scrape", "a", 1_500, 3_000, Ct), "the holder renews its own lease");
        Assert.False(await db.Store.TryAcquireLeaseAsync("scrape", "b", 3_000, 4_000, Ct), "a lease is live until strictly after its expiry");
        Assert.True(await db.Store.TryAcquireLeaseAsync("scrape", "b", 3_001, 4_000, Ct));
        Assert.False(await db.Store.TryAcquireLeaseAsync("scrape", "a", 3_500, 5_000, Ct));

        Assert.False(await db.Store.ReleaseLeaseAsync("scrape", "a", Ct), "only the holder can release");
        Assert.True(await db.Store.ReleaseLeaseAsync("scrape", "b", Ct));
        Assert.False(await db.Store.ReleaseLeaseAsync("scrape", "b", Ct));
        Assert.True(await db.Store.TryAcquireLeaseAsync("scrape", "a", 3_500, 5_000, Ct));

        // Names are independent.
        Assert.True(await db.Store.TryAcquireLeaseAsync("alerts", "b", 3_500, 5_000, Ct));
    }

    /// <summary>
    /// Replicas racing for a lease nobody holds: the unique key lets exactly one insert through, and the losers must
    /// see the winner's row and refuse rather than overwrite it.
    /// </summary>
    [Fact]
    public async Task Racing_for_a_free_lease_has_exactly_one_winner()
    {
        var db = await NewDatabaseAsync();
        var stores = Enumerable.Range(0, 8).Select(_ => OpsStore.Open(db.Settings)).ToArray();
        try
        {
            for (var round = 0; round < 5; round++)
            {
                var name = "round-" + round;
                var results = await Task.WhenAll(stores.Select((store, index) =>
                    store.TryAcquireLeaseAsync(name, "holder-" + index, 1_000, 2_000, Ct)));
                Assert.Single(results, won => won);
            }
        }
        finally
        {
            foreach (var store in stores)
            {
                store.Dispose();
            }
        }
    }

    // ---- events and the session projection ------------------------------------------------------------------

    [Fact]
    public async Task Events_are_stored_once_and_folded_into_a_session()
    {
        var db = await NewDatabaseAsync();
        var start = new DateTimeOffset(2026, 3, 1, 12, 0, 0, 123, TimeSpan.Zero);
        var batch = new[]
        {
            Event("e1", start, "c1"),
            Event("e2", start.AddSeconds(1), "c1", OpsEventKinds.SessionAccepted, new { hostDeviceId = 222_222_222u }),
            Event(
                "e3",
                start.AddSeconds(2),
                "c1",
                OpsEventKinds.SessionTelemetry,
                new
                {
                    role = "controller",
                    mode = "view",
                    transport = "direct",
                    rttMilliseconds = 12.5,
                    platform = "windows",
                    version = "1.2",
                    sessionId = "client-1",
                }),
            Event("e4", start.AddSeconds(60), "c1", OpsEventKinds.SessionEnded, new { reason = "closed" }),
        };

        Assert.Equal(4, await db.Store.AppendEventsAsync(batch, start.ToUnixTimeMilliseconds(), Ct));
        Assert.Equal(0, await db.Store.AppendEventsAsync(batch, start.ToUnixTimeMilliseconds() + 1, Ct));

        var session = Assert.Single(await db.Store.ListSessionsAsync(10, null, null, Ct));
        Assert.Equal("c1", session.CorrelationId);
        Assert.Equal(111_111_111u, session.ControllerDeviceId);
        Assert.Equal(222_222_222u, session.HostDeviceId);
        Assert.Equal(start.ToUnixTimeMilliseconds(), session.StartedAt);
        Assert.Equal(start.AddSeconds(60).ToUnixTimeMilliseconds(), session.EndedAt);
        Assert.Equal("closed", session.EndReason);
        Assert.Equal("view", session.Mode);
        Assert.Equal("direct", session.Transport);
        Assert.Equal(12.5, session.RttMs);
        Assert.Equal("windows", session.Platform);
        Assert.Equal("1.2", session.Version);
        Assert.Equal("client-1", session.ClientSessionId);
        Assert.Equal(session, await db.Store.FindSessionAsync(session.Id, Ct));
        Assert.Null(await db.Store.FindSessionAsync("missing", Ct));

        var events = await db.Store.ReadSessionEventsAsync("c1", Ct);
        Assert.Equal(["e1", "e2", "e3", "e4"], events.Select(row => row.EventId));
        Assert.Equal(start.ToUnixTimeMilliseconds(), events[0].OccurredAt);
        Assert.Equal(start.ToUnixTimeMilliseconds(), events[0].ReceivedAt);

        var mix = await db.Store.SummarizeSessionsAsync(0, Ct);
        Assert.Equal(1, mix.Direct);
        Assert.Equal(0, mix.Relayed);
        Assert.Equal(new KeyValuePair<string, int>("windows", 1), Assert.Single(mix.Platforms));
        Assert.Equal(12.5, await db.Store.AverageRttAsync(0, Ct));
        Assert.Null(await db.Store.AverageRttAsync(long.MaxValue, Ct));
    }

    /// <summary>Concurrent batches about one session must not overwrite each other's fields.</summary>
    [Fact]
    public async Task Concurrent_batches_for_one_session_lose_no_update()
    {
        var db = await NewDatabaseAsync();
        var start = DateTimeOffset.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 10).Select(index => db.Store.AppendEventsAsync(
            [Event("e" + index, start.AddSeconds(index), "c1")],
            start.ToUnixTimeMilliseconds(),
            Ct)));

        Assert.Equal(10, (await db.Store.ReadSessionEventsAsync("c1", Ct)).Count);
        var session = Assert.Single(await db.Store.ListSessionsAsync(10, null, null, Ct));
        Assert.Equal(start.AddSeconds(9).ToUnixTimeMilliseconds(), session.UpdatedAt);
    }

    [Fact]
    public async Task Sessions_page_newest_first_with_a_cursor_that_breaks_ties_by_id()
    {
        var db = await NewDatabaseAsync();
        var start = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        // Three sessions share a start; one is later. Pages of two must neither repeat nor skip across the tie.
        await db.Store.AppendEventsAsync(
            [
                Event("a", start, "c-a"),
                Event("b", start, "c-b"),
                Event("c", start, "c-c"),
                Event("d", start.AddMinutes(1), "c-d"),
            ],
            start.ToUnixTimeMilliseconds(),
            Ct);

        var seen = new List<OpsSessionRow>();
        long? before = null;
        string? beforeId = null;
        for (var page = 0; page < 5; page++)
        {
            var rows = await db.Store.ListSessionsAsync(2, before, beforeId, Ct);
            if (rows.Count == 0)
            {
                break;
            }

            seen.AddRange(rows);
            before = rows[^1].StartedAt;
            beforeId = rows[^1].Id;
        }

        Assert.Equal(4, seen.Count);
        Assert.Equal(4, seen.Select(row => row.Id).Distinct().Count());
        Assert.Equal("c-d", seen[0].CorrelationId);
        Assert.Equal(seen.Skip(1).Select(row => row.Id).OrderDescending(StringComparer.Ordinal), seen.Skip(1).Select(row => row.Id));
    }

    [Fact]
    public async Task A_relayed_session_reports_its_byte_counters_and_the_relay_node()
    {
        var db = await NewDatabaseAsync();
        var at = DateTimeOffset.UtcNow;

        await db.Store.AppendEventsAsync(
            [
                Event("r1", at, "c1", OpsEventKinds.RelayPaired, new { }),
                Event("r2", at.AddSeconds(5), "c1", OpsEventKinds.RelayClosed, new { reason = "idle", bytesToHost = 5_000_000_000L, bytesToController = 7L }),
            ],
            at.ToUnixTimeMilliseconds(),
            Ct);

        var session = Assert.Single(await db.Store.ListSessionsAsync(10, null, null, Ct));
        Assert.Equal("relay", session.Source);
        Assert.Equal("relay", session.Transport);
        Assert.Equal("node", session.RelayNodeId);
        Assert.Equal(5_000_000_000L, session.BytesIn);
        Assert.Equal(7L, session.BytesOut);
    }

    [Fact]
    public async Task Retention_deletes_only_the_history_it_was_asked_to_delete()
    {
        var db = await NewDatabaseAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        var recent = DateTimeOffset.UtcNow;

        await db.Store.WriteSamplesAsync("node", old.ToUnixTimeMilliseconds(), [new PrometheusSample("m", "m", 1)], Ct);
        await db.Store.WriteSamplesAsync("node", recent.ToUnixTimeMilliseconds(), [new PrometheusSample("m", "m", 2)], Ct);
        await db.Store.AppendEventsAsync([Event("old", old, "old-correlation")], old.ToUnixTimeMilliseconds(), Ct);
        await db.Store.AppendEventsAsync([Event("recent", recent, "recent-correlation")], recent.ToUnixTimeMilliseconds(), Ct);
        await db.Store.OpenAlertAsync(Alert("old-alert", old), Ct);
        await db.Store.ResolveAlertAsync("old-alert", old.ToUnixTimeMilliseconds(), Ct);
        await db.Store.OpenAlertAsync(Alert("recent-alert", recent), Ct);
        await db.Store.RecordNotificationAttemptAsync("old-alert", "webhook", "opened", sent: true, null, old.ToUnixTimeMilliseconds(), Ct);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();

        Assert.Equal(1, await db.Store.DeleteSamplesBeforeAsync(cutoff, cancellationToken: Ct));
        Assert.Equal(1, await db.Store.DeleteEventsBeforeAsync(cutoff, cancellationToken: Ct));
        Assert.Equal(1, await db.Store.DeleteSessionsBeforeAsync(cutoff, cancellationToken: Ct));
        Assert.Equal(1, await db.Store.DeleteResolvedAlertsBeforeAsync(cutoff, cancellationToken: Ct));

        var points = await db.Store.ReadSeriesAsync(["m"], 0, long.MaxValue, Ct);
        Assert.Equal(recent.ToUnixTimeMilliseconds(), Assert.Single(points).Timestamp);
        Assert.Empty(await db.Store.ReadSessionEventsAsync("old-correlation", Ct));
        Assert.Single(await db.Store.ReadSessionEventsAsync("recent-correlation", Ct));
        Assert.Equal("recent-correlation", Assert.Single(await db.Store.ListSessionsAsync(10, null, null, Ct)).CorrelationId);
        Assert.Equal("recent-alert", Assert.Single(await db.Store.ListAlertsAsync(null, 10, Ct)).Id);
        Assert.False(await db.Store.HasNotificationAttemptAsync("old-alert", "webhook", "opened", Ct), "attempts go with their alert");
    }

    [Fact]
    public async Task Retention_works_in_bounded_batches()
    {
        var db = await NewDatabaseAsync();
        var samples = Enumerable.Range(0, 25).Select(index => new PrometheusSample("m" + index, "m", index)).ToArray();
        await db.Store.WriteSamplesAsync("node", 1_000, samples, Ct);

        Assert.Equal(10, await db.Store.DeleteSamplesBeforeAsync(2_000, limit: 10, Ct));
        Assert.Equal(10, await db.Store.DeleteSamplesBeforeAsync(2_000, limit: 10, Ct));
        Assert.Equal(5, await db.Store.DeleteSamplesBeforeAsync(2_000, limit: 10, Ct));
        Assert.Equal(0, await db.Store.DeleteSamplesBeforeAsync(2_000, limit: 10, Ct));
    }

    // ---- metrics ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Samples_are_upserted_read_back_exactly_and_the_latest_one_wins()
    {
        var db = await NewDatabaseAsync();
        await db.Store.UpsertMetricCatalogAsync(
            [new("rd_bytes_total", "counter"), new("rd_online", "gauge")],
            Ct);
        await db.Store.UpsertMetricCatalogAsync([new("rd_online", "counter")], Ct);
        Assert.Equal(
            [new MetricCatalogRow("rd_bytes_total", "counter"), new MetricCatalogRow("rd_online", "counter")],
            await db.Store.ListMetricCatalogAsync(Ct));
        Assert.Equal("counter", await db.Store.ReadMetricKindAsync("rd_online", Ct));
        Assert.Null(await db.Store.ReadMetricKindAsync("unknown", Ct));

        // A float cannot hold 2^24 + 1, a double can; the retried scrape at the same timestamp replaces the value.
        await db.Store.WriteSamplesAsync("n1", 1_000, [new PrometheusSample("rd_bytes_total", "rd_bytes_total", 16_777_217)], Ct);
        await db.Store.WriteSamplesAsync("n1", 2_000, [new PrometheusSample("rd_bytes_total", "rd_bytes_total", 9_007_199_254_740_991d)], Ct);
        await db.Store.WriteSamplesAsync("n1", 2_000, [new PrometheusSample("rd_bytes_total", "rd_bytes_total", 0.1 + 0.2)], Ct);
        await db.Store.WriteSamplesAsync("n2", 1_500, [new PrometheusSample("rd_bytes_total", "rd_bytes_total", 5)], Ct);

        var series = await db.Store.ReadSeriesAsync(["rd_bytes_total"], 0, 10_000, Ct);
        Assert.Equal(
            [
                new MetricPoint("n1", "rd_bytes_total", 1_000, 16_777_217),
                new MetricPoint("n1", "rd_bytes_total", 2_000, 0.1 + 0.2),
                new MetricPoint("n2", "rd_bytes_total", 1_500, 5),
            ],
            series);

        Assert.Equal(
            [new MetricPoint("n1", "rd_bytes_total", 2_000, 0.1 + 0.2), new MetricPoint("n2", "rd_bytes_total", 1_500, 5)],
            await db.Store.ReadLatestAsync(["rd_bytes_total", "missing"], 0, Ct));
        // Readings older than the cutoff count as absent: n2's last reading is before it.
        Assert.Equal(
            [new MetricPoint("n1", "rd_bytes_total", 2_000, 0.1 + 0.2)],
            await db.Store.ReadLatestAsync(["rd_bytes_total"], 1_600, Ct));
        Assert.Equal(
            [new MetricPoint("n1", "rd_bytes_total", 2_000, 0.1 + 0.2)],
            await db.Store.ReadLatestForNodeAsync("n1", Ct));
    }

    /// <summary>More rows than one INSERT carries (the store chunks at 200), in one transaction.</summary>
    [Fact]
    public async Task A_large_scrape_is_written_in_chunks()
    {
        var db = await NewDatabaseAsync();
        var samples = Enumerable.Range(0, 450).Select(index => new PrometheusSample("metric_" + index, "metric", index)).ToArray();

        await db.Store.WriteSamplesAsync("n1", 1_000, samples, Ct);
        await db.Store.UpsertMetricCatalogAsync(
            samples.Select(sample => new KeyValuePair<string, string>(sample.Key, "gauge")).ToArray(),
            Ct);

        Assert.Equal(450, (await db.Store.ReadLatestForNodeAsync("n1", Ct)).Count);
        Assert.Equal(450, (await db.Store.ListMetricCatalogAsync(Ct)).Count);
        Assert.Equal(
            450,
            (await db.Store.ReadSeriesAsync(samples.Select(sample => sample.Key).ToArray(), 0, 2_000, Ct)).Count);
    }

    // ---- alerts, silences, actions ---------------------------------------------------------------------------

    [Fact]
    public async Task Alert_rules_are_upserted_with_their_exact_thresholds()
    {
        var db = await NewDatabaseAsync();
        var rule = new AlertRule(
            "r1", "High load", AlertRuleKinds.SingleMetric, "cpu", null, null, "avg", AlertComparators.GreaterThan, 16_777_217.5, 300, 60, "P2", true);

        await db.Store.UpsertAlertRuleAsync(rule, Ct);
        await db.Store.UpsertAlertRuleAsync(rule with { Threshold = 0.1 + 0.2, Enabled = false, Name = "Higher load" }, Ct);
        await db.Store.UpsertAlertRuleAsync(
            rule with { Id = "r2", Name = "Ratio", Kind = AlertRuleKinds.Ratio, Metric = null, Numerator = "a", Denominator = "b" },
            Ct);

        var rules = await db.Store.ListAlertRulesAsync(Ct);
        Assert.Equal(2, rules.Count);
        Assert.Equal(2, await db.Store.CountAlertRulesAsync(Ct));
        Assert.Equal(rule with { Threshold = 0.1 + 0.2, Enabled = false, Name = "Higher load" }, rules.Single(candidate => candidate.Id == "r1"));
        var ratio = rules.Single(candidate => candidate.Id == "r2");
        Assert.Null(ratio.Metric);
        Assert.Equal(("a", "b"), (ratio.Numerator, ratio.Denominator));

        Assert.True(await db.Store.DeleteAlertRuleAsync("r1", Ct));
        Assert.False(await db.Store.DeleteAlertRuleAsync("r1", Ct));
    }

    [Fact]
    public async Task Alerts_open_acknowledge_resolve_and_filter_by_state()
    {
        var db = await NewDatabaseAsync();
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.Store.OpenAlertAsync(Alert("a1", DateTimeOffset.FromUnixTimeMilliseconds(at)) with { Value = 16_777_217.5 }, Ct);
        await db.Store.OpenAlertAsync(Alert("a2", DateTimeOffset.FromUnixTimeMilliseconds(at + 1)) with { Value = null }, Ct);

        Assert.Equal(16_777_217.5, (await db.Store.FindOpenAlertAsync("rule:a1", Ct))!.Value);
        Assert.Null((await db.Store.FindOpenAlertAsync("rule:a2", Ct))!.Value);
        Assert.Null(await db.Store.FindOpenAlertAsync("rule:none", Ct));
        Assert.Equal(["a1", "a2"], (await db.Store.ListOpenAlertsAsync(Ct)).Select(alert => alert.Id));
        Assert.Equal(2, await db.Store.CountAlertsSinceAsync(at, Ct));

        Assert.True(await db.Store.AckAlertAsync("a1", "alice", at + 10, Ct));
        Assert.False(await db.Store.AckAlertAsync("a1", "bob", at + 11, Ct), "already acknowledged");
        Assert.True(await db.Store.ResolveAlertAsync("a1", at + 20, Ct));
        Assert.False(await db.Store.ResolveAlertAsync("a1", at + 21, Ct));
        Assert.False(await db.Store.AckAlertAsync("a1", "alice", at + 22, Ct), "resolved alerts cannot be acknowledged");

        var resolved = Assert.Single(await db.Store.ListAlertsAsync("resolved", 10, Ct));
        Assert.Equal(("a1", "alice", at + 10, at + 20), (resolved.Id, resolved.AckedBy, resolved.AckedAt, resolved.ResolvedAt));
        Assert.Equal("a2", Assert.Single(await db.Store.ListAlertsAsync("open", 10, Ct)).Id);
        Assert.Equal(["a2", "a1"], (await db.Store.ListAlertsAsync(null, 10, Ct)).Select(alert => alert.Id));
        Assert.Equal(["a2"], (await db.Store.ListAlertsAsync("", 1, Ct)).Select(alert => alert.Id));
    }

    [Fact]
    public async Task A_failed_notification_is_pending_until_it_succeeds_or_runs_out_of_attempts()
    {
        var db = await NewDatabaseAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.Store.OpenAlertAsync(Alert("alert", DateTimeOffset.UtcNow), Ct);

        Assert.False(await db.Store.HasNotificationAttemptAsync("alert", "webhook", "opened", Ct));
        await db.Store.RecordNotificationAttemptAsync("alert", "webhook", "opened", sent: false, "HTTP 500", now, Ct);
        await db.Store.RecordNotificationAttemptAsync("alert", "webhook", "opened", sent: false, "HTTP 502", now + 500, Ct);

        var item = Assert.Single(await db.Store.ListPendingNotificationsAsync("webhook", NotificationBackoff.MaxAttempts, cancellationToken: Ct));
        Assert.Equal(("opened", 2, now + 500), (item.Transition, item.Attempts, item.LastAttemptAt));
        Assert.Empty(await db.Store.ListPendingNotificationsAsync("webhook", maxAttempts: 2, cancellationToken: Ct));
        Assert.Empty(await db.Store.ListPendingNotificationsAsync("other-channel", NotificationBackoff.MaxAttempts, cancellationToken: Ct));

        await db.Store.RecordNotificationAttemptAsync("alert", "webhook", "opened", sent: true, null, now + 1000, Ct);
        Assert.True(await db.Store.HasNotificationAttemptAsync("alert", "webhook", "opened", Ct));
        Assert.Empty(await db.Store.ListPendingNotificationsAsync("webhook", NotificationBackoff.MaxAttempts, cancellationToken: Ct));
    }

    [Fact]
    public async Task Silences_apply_only_inside_their_window_and_can_be_ended_early()
    {
        var db = await NewDatabaseAsync();
        await db.Store.CreateSilenceAsync(new OpsSilenceRow("s1", "rule-1", null, "maintenance", "alice", 1_000, 5_000), Ct);
        await db.Store.CreateSilenceAsync(new OpsSilenceRow("s2", null, "relay-1", "", "bob", 1_000, 3_000), Ct);

        Assert.Equal(["s2", "s1"], (await db.Store.ListActiveSilencesAsync(2_000, Ct)).Select(row => row.Id));
        Assert.Equal(["s1"], (await db.Store.ListActiveSilencesAsync(3_000, Ct)).Select(row => row.Id));
        Assert.Empty(await db.Store.ListActiveSilencesAsync(500, Ct));
        Assert.Empty(await db.Store.ListActiveSilencesAsync(5_000, Ct));

        Assert.Equal(new OpsSilenceRow("s1", "rule-1", null, "maintenance", "alice", 1_000, 5_000), await db.Store.FindSilenceAsync("s1", Ct));
        Assert.Equal("relay-1", (await db.Store.FindSilenceAsync("s2", Ct))!.Node);
        Assert.True(await db.Store.DeleteSilenceAsync("s1", Ct));
        Assert.False(await db.Store.DeleteSilenceAsync("s1", Ct));
        Assert.Null(await db.Store.FindSilenceAsync("s1", Ct));
    }

    [Fact]
    public async Task Management_actions_need_a_second_person_and_complete_once()
    {
        var db = await NewDatabaseAsync();
        var action = new OpsAdminActionRow(
            "action", ActionKinds.RevokeDevice, "node", "123456789", null, "{}", ActionStates.Pending, "operator", 1_000, null, null, null, null);
        await db.Store.CreateActionAsync(action, Ct);
        await db.Store.CreateActionAsync(action with { Id = "other", RequestedAt = 2_000, SessionId = "session" }, Ct);

        Assert.Equal(["other", "action"], (await db.Store.ListActionsAsync(null, 10, Ct)).Select(row => row.Id));
        Assert.Equal(["action", "other"], (await db.Store.ListActionsAsync(ActionStates.Pending, 10, Ct)).Select(row => row.Id).Order());
        Assert.Equal("session", (await db.Store.FindActionAsync("other", Ct))!.SessionId);

        Assert.Null(await db.Store.ApproveActionAsync("action", "operator", 3_000, Ct));
        var approved = await db.Store.ApproveActionAsync("action", "admin", 3_000, Ct);
        Assert.Equal((ActionStates.Approved, "admin", 3_000L), (approved!.State, approved.ApprovedBy, approved.ApprovedAt));
        Assert.Null(await db.Store.ApproveActionAsync("action", "someone-else", 3_001, Ct));

        Assert.Empty(await db.Store.ListStaleApprovedActionsAsync(3_000, cancellationToken: Ct));
        Assert.Equal("action", Assert.Single(await db.Store.ListStaleApprovedActionsAsync(3_001, cancellationToken: Ct)).Id);

        Assert.True(await db.Store.CompleteActionAsync("action", ActionStates.Failed, 4_000, "node refused", Ct));
        Assert.False(await db.Store.CompleteActionAsync("action", ActionStates.Executed, 4_001, null, Ct));
        var closed = await db.Store.FindActionAsync("action", Ct);
        Assert.Equal((ActionStates.Failed, 4_000L, "node refused"), (closed!.State, closed.ExecutedAt, closed.Error));
        Assert.Equal(["other"], (await db.Store.ListActionsAsync(ActionStates.Pending, 10, Ct)).Select(row => row.Id));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private async Task<PostgresDatabase> NewDatabaseAsync(bool ensureSchema = true)
    {
        var database = PostgresDatabase.Create();
        _databases.Add(database);
        if (ensureSchema)
        {
            await database.Store.EnsureSchemaAsync(Ct);
        }

        return database;
    }

    private static OpsAuditEntry Audit(string target, long timestamp) => new(
        Guid.NewGuid().ToString("N"),
        timestamp,
        "test",
        "test.append",
        target,
        "{}",
        "-",
        "success");

    private static OpsEventEnvelope Event(
        string id,
        DateTimeOffset at,
        string correlation,
        string kind = OpsEventKinds.SessionRequested,
        object? payload = null) => new()
        {
            V = 1,
            EventId = id,
            Node = "node",
            Kind = kind,
            OccurredAt = at,
            CorrelationId = correlation,
            Payload = JsonSerializer.Serialize(
                payload ?? new { controllerDeviceId = 111_111_111u, hostDeviceId = 222_222_222u },
                OpsEventJson.Options),
        };

    private static OpsAdminUser User(string name, string role, long createdAt) => new(
        Guid.NewGuid().ToString("N"),
        name,
        PasswordHasher.Hash("correct horse battery staple"),
        Totp.GenerateSecret(),
        role,
        Disabled: false,
        createdAt);

    private static OpsAlertRow Alert(string id, DateTimeOffset openedAt) => new(
        id,
        "rule",
        "P2",
        "rule:" + id,
        "open",
        1,
        "{}",
        openedAt.ToUnixTimeMilliseconds(),
        null,
        null,
        null);

    /// <summary>A throw-away schema on the server named by <c>RD_TEST_POSTGRES</c>, with a store opened on it.</summary>
    private sealed class PostgresDatabase : IAsyncDisposable
    {
        private const string EnvironmentVariable = "RD_TEST_POSTGRES";

        private readonly string _adminConnectionString;
        private readonly string _schema;

        private PostgresDatabase(string adminConnectionString, string schema)
        {
            _adminConnectionString = adminConnectionString;
            _schema = schema;

            // Pooling is off so that dropping the schema never races a pooled connection and no pool outlives its test.
            Settings = new OpsDatabaseSettings
            {
                Provider = OpsDatabaseSettings.Postgres,
                ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
                {
                    SearchPath = schema,
                    Pooling = false,
                }.ConnectionString,
            };
            Store = OpsStore.Open(Settings);
        }

        public OpsDatabaseSettings Settings { get; }

        public OpsStore Store { get; }

        public static PostgresDatabase Create()
        {
            var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
            Assert.SkipWhen(
                string.IsNullOrWhiteSpace(connectionString),
                "PostgreSQL tests are opt-in. Start a server, for example "
                + "'docker run -d --name rd-pg-test -e POSTGRES_PASSWORD=test -p 127.0.0.1:55432:5432 postgres:18', "
                + $"and set {EnvironmentVariable} to its Npgsql connection string "
                + "(Host=127.0.0.1;Port=55432;Username=postgres;Password=test;Database=postgres).");

            var schema = "rd_test_" + Guid.NewGuid().ToString("N");
            var admin = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
            using var connection = new NpgsqlConnection(admin);
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\";";
            create.ExecuteNonQuery();

            return new PostgresDatabase(admin, schema);
        }

        /// <summary>Runs a script directly, to arrange a database the way an older build left it.</summary>
        public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(Settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>The first column of every row a query returns.</summary>
        public async Task<List<T>> ColumnAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(Settings.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            var values = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                values.Add(reader.GetFieldValue<T>(0));
            }

            return values;
        }

        public async ValueTask DisposeAsync()
        {
            Store.Dispose();
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE;";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
