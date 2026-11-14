using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Ops;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// Two consoles evaluating against one database must not page twice. The lease is what stands between a slow
/// notification channel and a duplicate page, and the open alert row is what stands between two sequential passes and
/// the same duplicate.
/// </summary>
/// <remarks>
/// The channel here blocks on the first send instead of sleeping for a fixed five seconds: the test needs the second
/// evaluation to run while the first one is demonstrably still inside <see cref="INotificationChannel.SendAsync"/>,
/// and a sleep would make that a race. Each service gets its own <see cref="OpsLease"/>, which is how two processes
/// differ — a shared lease would let the test pass without the database doing any arbitration.
/// </remarks>
public sealed class AlertLeaseNotificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_slow_notification_is_sent_once_across_two_evaluating_instances()
    {
        await using var db = await TempDatabase.CreateAsync();
        var rule = new AlertRule(
            "rule-lease",
            "节点抓取失败",
            AlertRuleKinds.Scrape,
            Metric: null,
            Numerator: null,
            Denominator: null,
            Aggregation: "avg",
            Comparator: AlertComparators.GreaterThan,
            Threshold: 0.5,
            WindowSeconds: 60,
            ForSeconds: 0,
            Severity: "P1",
            Enabled: true);
        await db.Store.UpsertAlertRuleAsync(rule, Ct);

        var node = new OpsNode(
            "node-lease",
            "lease-node",
            "rendezvous",
            "http://127.0.0.1:1",
            Region: null,
            Enabled: true,
            LastScrapeAt: null,
            LastScrapeOk: null,
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await db.Store.CreateNodeAsync(node, Ct);
        await db.Store.RecordScrapeAsync(node.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ok: false, Ct);

        var channel = new BlockingChannel();
        var time = TimeProvider.System;
        var (first, firstLease) = CreateEvaluation(db.Store, channel, time);
        var (second, secondLease) = CreateEvaluation(db.Store, channel, time);

        // The first pass reaches the channel and stays there; the second pass has to be turned away by the lease,
        // not by a check that happens to be fast enough.
        var firstPass = first.RunOnceAsync(Ct);
        await channel.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await second.RunOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(1, channel.Sends);
        Assert.Single(await db.Store.ListOpenAlertsAsync(Ct));

        // Let the first pass finish and record its attempt. Releasing both leases is deliberate: the next pass must be
        // stopped by the open alert row, which is the other half of "only once" and the one that survives a restart.
        channel.ReleaseFirstSend();
        await firstPass.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await firstLease.ReleaseAsync("alerts", Ct);
        await secondLease.ReleaseAsync("alerts", Ct);

        await second.RunOnceAsync(Ct);

        Assert.Equal(1, channel.Sends);

        // A recovery is the same contract in the other direction: one resolved page, not one per instance.
        await db.Store.RecordScrapeAsync(
            node.Id,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ok: true,
            Ct);
        await second.RunOnceAsync(Ct);

        Assert.Equal(["opened", "resolved"], channel.Transitions);
        Assert.Empty(await db.Store.ListOpenAlertsAsync(Ct));
    }

    /// <summary>
    /// A pass may retry only a couple of deliveries per channel: each send can wait out a channel timeout, and a
    /// backlog must not hold the alert lease for minutes. The third one stays owed for the next pass.
    /// </summary>
    [Fact]
    public async Task One_pass_retries_at_most_two_notifications_per_channel()
    {
        await using var db = await TempDatabase.CreateAsync();
        var rule = new AlertRule(
            "rule-batch",
            "节点抓取失败",
            AlertRuleKinds.Scrape,
            Metric: null,
            Numerator: null,
            Denominator: null,
            Aggregation: "avg",
            Comparator: AlertComparators.GreaterThan,
            Threshold: 0.5,
            WindowSeconds: 60,
            ForSeconds: 0,
            Severity: "P1",
            Enabled: true);
        await db.Store.UpsertAlertRuleAsync(rule, Ct);

        var now = DateTimeOffset.UtcNow;
        var channel = new CountingChannel();
        for (var i = 0; i < 3; i++)
        {
            var node = new OpsNode(
                $"node-batch-{i}",
                $"batch-{i}",
                "rendezvous",
                "http://127.0.0.1:1",
                Region: null,
                Enabled: true,
                LastScrapeAt: null,
                LastScrapeOk: null,
                CreatedAt: now.ToUnixTimeMilliseconds());
            await db.Store.CreateNodeAsync(node, Ct);
            await db.Store.RecordScrapeAsync(node.Id, now.ToUnixTimeMilliseconds(), ok: false, Ct);

            var alert = new OpsAlertRow(
                $"alert-batch-{i}",
                rule.Id,
                "P1",
                AlertDedupeKeys.ForNode(rule.Id, node.Name),
                "open",
                1,
                "{}",
                now.AddHours(-1).ToUnixTimeMilliseconds(),
                null,
                null,
                null);
            await db.Store.OpenAlertAsync(alert, Ct);
            await db.Store.RecordNotificationAttemptAsync(
                alert.Id,
                channel.Id,
                "opened",
                sent: false,
                "the first delivery failed",
                now.AddHours(-1).ToUnixTimeMilliseconds(),
                Ct);
        }

        var alerts = new AlertService(
            db.Store,
            new AlertStateMachine(TimeProvider.System),
            [channel],
            TimeProvider.System,
            NullLogger<AlertService>.Instance);

        await alerts.EvaluateOnceAsync(Ct);
        Assert.Equal(2, channel.Sends);

        // The two that were sent are no longer owed; the third is, and the next pass delivers it.
        await alerts.EvaluateOnceAsync(Ct);
        Assert.Equal(3, channel.Sends);
    }

    private static (AlertEvaluationService Service, OpsLease Lease) CreateEvaluation(
        OpsStore store,
        INotificationChannel channel,
        TimeProvider time)
    {
        var alerts = new AlertService(
            store,
            new AlertStateMachine(time),
            [channel],
            time,
            NullLogger<AlertService>.Instance);
        var lease = new OpsLease(store, time);
        var options = new OpsOptions { AlertIntervalSeconds = 5 };
        var service = new AlertEvaluationService(
            alerts,
            options,
            lease,
            time,
            NullLogger<AlertEvaluationService>.Instance);
        return (service, lease);
    }

    /// <summary>Counts deliveries without delaying them.</summary>
    private sealed class CountingChannel : INotificationChannel
    {
        private int _sends;

        public string Id => "counting";

        public bool IsConfigured => true;

        public int Sends => Volatile.Read(ref _sends);

        public Task<string?> SendAsync(
            OpsAlertRow alert,
            string ruleName,
            string severity,
            string transition,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sends);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>A channel whose first send blocks until the test has run its second instance.</summary>
    private sealed class BlockingChannel : INotificationChannel
    {
        private readonly TaskCompletionSource _firstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _transitions = new();
        private int _sends;

        public string Id => "blocking";

        public bool IsConfigured => true;

        public int Sends => Volatile.Read(ref _sends);

        public IReadOnlyCollection<string> Transitions => _transitions;

        public Task FirstSendStarted => _firstSendStarted.Task;

        public void ReleaseFirstSend() => _releaseFirstSend.TrySetResult();

        public async Task<string?> SendAsync(
            OpsAlertRow alert,
            string ruleName,
            string severity,
            string transition,
            CancellationToken cancellationToken)
        {
            _transitions.Enqueue(transition);
            if (Interlocked.Increment(ref _sends) == 1)
            {
                _firstSendStarted.TrySetResult();
                await _releaseFirstSend.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
    }

    /// <summary>A schema-initialized SQLite database in a directory of its own.</summary>
    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _directory;

        private TempDatabase(string directory, OpsStore store)
        {
            _directory = directory;
            Store = store;
        }

        public OpsStore Store { get; }

        public static async Task<TempDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"ops-alert-lease-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var store = OpsStore.Open(new OpsDatabaseSettings
            {
                Provider = OpsDatabaseSettings.Sqlite,
                Path = Path.Combine(directory, "ops.db"),
            });
            await store.EnsureSchemaAsync(Ct);
            return new TempDatabase(directory, store);
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
