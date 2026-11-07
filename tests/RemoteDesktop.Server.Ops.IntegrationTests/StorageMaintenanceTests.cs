using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The storage operations the background services call: retention, notification retry, action recovery, lease renewal,
/// single-use TOTP steps and the audit lock. These are tested at the store because they are SQL, not HTTP.
/// </summary>
public class StorageMaintenanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Retention_deletes_only_the_history_it_was_asked_to_delete()
    {
        await using var db = await TempDatabase.CreateAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        var recent = DateTimeOffset.UtcNow;

        await db.Store.WriteSamplesAsync(
            "node",
            old.ToUnixTimeMilliseconds(),
            [new PrometheusSample("m", "m", 1)],
            Ct);
        await db.Store.WriteSamplesAsync(
            "node",
            recent.ToUnixTimeMilliseconds(),
            [new PrometheusSample("m", "m", 2)],
            Ct);

        await db.Store.AppendEventsAsync(
            [Event("old", old, "old-correlation")],
            old.ToUnixTimeMilliseconds(),
            Ct);
        await db.Store.AppendEventsAsync(
            [Event("recent", recent, "recent-correlation")],
            recent.ToUnixTimeMilliseconds(),
            Ct);

        await db.Store.OpenAlertAsync(Alert("old-alert", old), Ct);
        await db.Store.ResolveAlertAsync("old-alert", old.ToUnixTimeMilliseconds(), Ct);
        await db.Store.OpenAlertAsync(Alert("recent-alert", recent), Ct);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();

        Assert.Equal(1, await db.Store.DeleteSamplesBeforeAsync(cutoff, cancellationToken: Ct));
        Assert.Equal(1, await db.Store.DeleteEventsBeforeAsync(cutoff, cancellationToken: Ct));
        Assert.Equal(1, await db.Store.DeleteSessionsBeforeAsync(cutoff, cancellationToken: Ct));
        Assert.Equal(1, await db.Store.DeleteResolvedAlertsBeforeAsync(cutoff, cancellationToken: Ct));

        var points = await db.Store.ReadSeriesAsync(["m"], 0, long.MaxValue, Ct);
        Assert.Single(points);
        Assert.Equal(recent.ToUnixTimeMilliseconds(), points[0].Timestamp);

        var oldEvents = await db.Store.ReadSessionEventsAsync("old-correlation", Ct);
        Assert.Empty(oldEvents);
        var recentEvents = await db.Store.ReadSessionEventsAsync("recent-correlation", Ct);
        Assert.Single(recentEvents);

        var sessions = await db.Store.ListSessionsAsync(10, null, null, Ct);
        Assert.Single(sessions);
        Assert.Equal("recent-correlation", sessions[0].CorrelationId);

        var alerts = await db.Store.ListAlertsAsync(null, 10, Ct);
        Assert.Single(alerts);
        Assert.Equal("recent-alert", alerts[0].Id);
    }

    /// <summary>A retention window of zero days means "keep forever" for that table, not "delete everything".</summary>
    [Fact]
    public async Task A_retention_window_of_zero_days_keeps_that_history_forever()
    {
        await using var db = await TempDatabase.CreateAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-400);
        await SeedOldHistoryAsync(db, old);

        var service = RetentionFor(db, new RetentionSettings { SampleDays = 0, EventDays = 0, SessionDays = 0, AlertDays = 0 });
        await service.RunOnceAsync(Ct);

        Assert.Single(await db.Store.ReadSeriesAsync(["m"], 0, long.MaxValue, Ct));
        Assert.Single(await db.Store.ReadSessionEventsAsync("old-correlation", Ct));
        Assert.Single(await db.Store.ListSessionsAsync(10, null, null, Ct));
        Assert.Single(await db.Store.ListAlertsAsync(null, 10, Ct));
    }

    [Fact]
    public async Task Retention_keeps_downsampled_metrics_longer_than_raw_samples()
    {
        await using var db = await TempDatabase.CreateAsync();
        var rawExpired = DateTimeOffset.UtcNow.AddDays(-100);
        var rollupExpired = DateTimeOffset.UtcNow.AddDays(-400);
        await db.Store.WriteSamplesAsync("node", rawExpired.ToUnixTimeMilliseconds(), [new PrometheusSample("m", "m", 1)], Ct);
        await db.Store.WriteSamplesAsync("node", rollupExpired.ToUnixTimeMilliseconds(), [new PrometheusSample("m", "m", 2)], Ct);
        await db.Store.RollupSamplesAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stepSeconds: 60, cancellationToken: Ct);

        var service = RetentionFor(db, new RetentionSettings { SampleDays = 30, RollupDays = 365, EventDays = 0, SessionDays = 0, AlertDays = 0 });
        await service.RunOnceAsync(Ct);

        Assert.Empty(await db.Store.ReadSeriesAsync(["m"], 0, long.MaxValue, Ct));
        var rawExpiredBucket = rawExpired.ToUnixTimeMilliseconds() / 60_000;
        var rollupExpiredBucket = rollupExpired.ToUnixTimeMilliseconds() / 60_000;
        Assert.Single(await db.Store.GetMetricRollupsAsync("node", "m", stepSeconds: 60, rawExpiredBucket, rawExpiredBucket, Ct));
        Assert.Empty(await db.Store.GetMetricRollupsAsync("node", "m", stepSeconds: 60, rollupExpiredBucket, rollupExpiredBucket, Ct));
    }

    [Fact]
    public async Task Retention_cleans_only_the_tables_whose_window_is_set()
    {
        await using var db = await TempDatabase.CreateAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-400);
        await SeedOldHistoryAsync(db, old);

        var service = RetentionFor(db, new RetentionSettings { SampleDays = 30, EventDays = 0, SessionDays = 0, AlertDays = 0 });
        await service.RunOnceAsync(Ct);

        Assert.Empty(await db.Store.ReadSeriesAsync(["m"], 0, long.MaxValue, Ct));
        Assert.Single(await db.Store.ReadSessionEventsAsync("old-correlation", Ct));
        Assert.Single(await db.Store.ListSessionsAsync(10, null, null, Ct));
        Assert.Single(await db.Store.ListAlertsAsync(null, 10, Ct));
    }

    private static RetentionService RetentionFor(TempDatabase db, RetentionSettings retention) => new(
        db.Store,
        new OpsOptions { Retention = retention },
        new OpsLease(db.Store, TimeProvider.System),
        TimeProvider.System,
        NullLogger<RetentionService>.Instance);

    private static async Task SeedOldHistoryAsync(TempDatabase db, DateTimeOffset old)
    {
        await db.Store.WriteSamplesAsync("node", old.ToUnixTimeMilliseconds(), [new PrometheusSample("m", "m", 1)], Ct);
        await db.Store.AppendEventsAsync([Event("old", old, "old-correlation")], old.ToUnixTimeMilliseconds(), Ct);
        await db.Store.OpenAlertAsync(Alert("old-alert", old), Ct);
        await db.Store.ResolveAlertAsync("old-alert", old.ToUnixTimeMilliseconds(), Ct);
    }

    [Fact]
    public async Task A_failed_notification_is_pending_until_it_succeeds_or_runs_out_of_attempts()
    {
        await using var db = await TempDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.Store.OpenAlertAsync(Alert("alert", DateTimeOffset.UtcNow), Ct);

        await db.Store.RecordNotificationAttemptAsync("alert", "webhook", "opened", sent: false, "HTTP 500", now, Ct);
        var pending = await db.Store.ListPendingNotificationsAsync("webhook", NotificationBackoff.MaxAttempts, cancellationToken: Ct);
        var item = Assert.Single(pending);
        Assert.Equal("opened", item.Transition);
        Assert.Equal(1, item.Attempts);
        Assert.Equal(now, item.LastAttemptAt);

        await db.Store.RecordNotificationAttemptAsync("alert", "webhook", "opened", sent: true, null, now + 1000, Ct);
        Assert.Empty(await db.Store.ListPendingNotificationsAsync("webhook", NotificationBackoff.MaxAttempts, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_stale_approved_action_is_visible_to_recovery_and_can_be_closed()
    {
        await using var db = await TempDatabase.CreateAsync();
        var old = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        var action = new OpsAdminActionRow(
            "action",
            ActionKinds.RevokeDevice,
            "node",
            "123456789",
            null,
            "{}",
            ActionStates.Pending,
            "operator",
            old,
            null,
            null,
            null,
            null);

        await db.Store.CreateActionAsync(action, Ct);
        Assert.NotNull(await db.Store.ApproveActionAsync("action", "admin", old, Ct));

        var stale = await db.Store.ListStaleApprovedActionsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken: Ct);
        Assert.Single(stale);

        await db.Store.CompleteActionAsync("action", ActionStates.Failed, old + 1, "recovered", Ct);
        var closed = await db.Store.FindActionAsync("action", Ct);
        Assert.Equal(ActionStates.Failed, closed!.State);
        Assert.Empty(await db.Store.ListStaleApprovedActionsAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken: Ct));
    }

    [Fact]
    public async Task A_totp_step_can_be_consumed_once_and_only_forward()
    {
        await using var db = await TempDatabase.CreateAsync();
        var user = new OpsAdminUser(
            "user",
            "alice",
            PasswordHasher.Hash("correct horse battery staple"),
            Totp.GenerateSecret(),
            OpsRoles.Admin,
            Disabled: false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await db.Store.CreateUserAsync(user, Ct);

        Assert.True(await db.Store.TryConsumeTotpStepAsync(user.Id, 100, Ct));
        Assert.False(await db.Store.TryConsumeTotpStepAsync(user.Id, 100, Ct));
        Assert.False(await db.Store.TryConsumeTotpStepAsync(user.Id, 99, Ct));
        Assert.True(await db.Store.TryConsumeTotpStepAsync(user.Id, 101, Ct));
    }

    [Fact]
    public async Task The_last_enabled_administrator_cannot_be_disabled_or_deleted()
    {
        await using var db = await TempDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var first = User("first", OpsRoles.Admin, now);
        var second = User("second", OpsRoles.Admin, now);
        await db.Store.CreateUserAsync(first, Ct);
        await db.Store.CreateUserAsync(second, Ct);

        Assert.True(await db.Store.SetUserDisabledAsync(first.Id, true, Ct));
        Assert.False(await db.Store.SetUserDisabledAsync(second.Id, true, Ct));
        Assert.False(await db.Store.DeleteUserAsync(second.Id, Ct));
        Assert.Equal(1, await db.Store.CountEnabledAdminsAsync(Ct));
    }

    [Fact]
    public async Task Two_store_instances_append_one_audit_chain()
    {
        await using var db = await TempDatabase.CreateAsync();
        using var second = OpsStore.Open(db.Settings);

        var tasks = Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? db.Store : second).AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "test",
                "test.append",
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "{}",
                "-",
                "success"),
            Ct));

        await Task.WhenAll(tasks);

        var records = await db.Store.ReadAuditChainAsync(Ct);
        Assert.Equal(20, records.Count);
        Assert.True(AuditVerifier.Verify(records).IsValid, "the chain must not fork when two stores append concurrently");
    }

    [Fact]
    public async Task A_second_process_cannot_take_a_live_lease_but_can_once_it_expires()
    {
        await using var db = await TempDatabase.CreateAsync();
        var time = new ManualClock();
        var first = new OpsLease(db.Store, time);
        var second = new OpsLease(db.Store, time);
        var duration = TimeSpan.FromSeconds(90);

        Assert.True(await first.TryAcquireAsync("scrape", duration, Ct));
        Assert.False(await second.TryAcquireAsync("scrape", duration, Ct));
        Assert.True(await first.TryAcquireAsync("scrape", duration, Ct), "the holder renews its own lease");

        time.Advance(duration + TimeSpan.FromSeconds(1));
        Assert.True(await second.TryAcquireAsync("scrape", duration, Ct));
        Assert.False(await first.TryAcquireAsync("scrape", duration, Ct));
    }

    [Fact]
    public async Task A_released_lease_can_be_taken_immediately_and_only_by_its_holder_to_release()
    {
        await using var db = await TempDatabase.CreateAsync();
        var time = new ManualClock();
        var first = new OpsLease(db.Store, time);
        var second = new OpsLease(db.Store, time);
        var duration = TimeSpan.FromMinutes(5);

        Assert.True(await first.TryAcquireAsync("alerts", duration, Ct));

        // A process that does not hold the lease cannot free it for the real holder.
        await second.ReleaseAsync("alerts", Ct);
        Assert.False(await second.TryAcquireAsync("alerts", duration, Ct));

        await first.ReleaseAsync("alerts", Ct);
        Assert.True(await second.TryAcquireAsync("alerts", duration, Ct), "a restarted console must not wait out the old lease");

        // Releasing a lease that is not held, or twice, is not an error.
        await first.ReleaseAsync("alerts", Ct);
        await first.ReleaseAsync("never-taken", Ct);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(10, 30)]
    [InlineData(30, 90)]
    [InlineData(60, 180)]
    public void A_lease_lasts_three_polling_intervals_but_never_less_than_thirty_seconds(int intervalSeconds, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OpsLease.DurationFor(TimeSpan.FromSeconds(intervalSeconds)));
    }

    private static OpsEventEnvelope Event(string id, DateTimeOffset at, string correlation) => new()
    {
        V = 1,
        EventId = id,
        Node = "node",
        Kind = OpsEventKinds.SessionRequested,
        OccurredAt = at,
        CorrelationId = correlation,
        Payload = JsonSerializer.Serialize(new { controllerDeviceId = 1u, hostDeviceId = 2u }, OpsEventJson.Options),
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

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private readonly string _directory;

        private TempDatabase(string directory, string path)
        {
            _directory = directory;
            _path = path;
        }

        public OpsStore Store { get; private set; } = null!;

        public OpsDatabaseSettings Settings => new() { Provider = OpsDatabaseSettings.Sqlite, Path = _path };

        public static async Task<TempDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"ops-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var db = new TempDatabase(directory, Path.Combine(directory, "ops.db"));
            db.Store = OpsStore.Open(db.Settings);
            await db.Store.EnsureSchemaAsync(Ct);
            return db;
        }

        public async ValueTask DisposeAsync()
        {
            Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A pooled handle on Windows; the temp directory is cleaned by the OS later.
            }

            GC.SuppressFinalize(this);
        }
    }
}
