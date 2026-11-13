using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Rendezvous;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// What a controller's heartbeat carries, from a real session: its role, the measured round trip, and none of the
/// host-only facts.
/// </summary>
public sealed class ControllerTelemetryTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_controller_heartbeat_names_its_role_carries_the_round_trip_and_leaves_the_platform_to_the_host()
    {
        var reports = new RecordingRendezvous();
        var clock = new StepClock();
        var telemetry = new TelemetryOptions { Enabled = true, IntervalSeconds = 30 };
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server, services => services
            .AddSingleton(new SessionTelemetry(telemetry, reports, clock)));
        await using var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        // The heartbeat waits five seconds so that a ping has come back; the report then carries what it measured.
        await WaitForAsync(() => session.RoundTripTime is not null, "the first round trip to be measured");
        await WaitForAsync(() => clock.PendingTimers > 0, "the heartbeat to start waiting");
        clock.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(100, Ct);
        Assert.Empty(reports.Heartbeats);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => reports.Heartbeats.Count == 1, "the first heartbeat");

        var report = Assert.Single(reports.Heartbeats);
        Assert.Equal(session.Context.Id.ToString(), report.SessionId);
        Assert.Equal(TelemetryRole.Controller, report.Role);
        Assert.Equal("control", report.Mode);
        Assert.Equal(TelemetryTransport.Direct, report.Transport);
        Assert.InRange(report.RttMillis, 0u, 60_000u);

        // Only the host knows which platform is being controlled; a controller's own would answer another question.
        Assert.Empty(report.Platform);
    }

    [Fact]
    public async Task A_controller_stops_reporting_when_its_session_closes()
    {
        var reports = new RecordingRendezvous();
        var clock = new StepClock();
        var telemetry = new TelemetryOptions { Enabled = true, IntervalSeconds = 5 };
        await using var host = await TestDevice.StartAsync(server);
        await using var controller = await TestDevice.StartAsync(server, services => services
            .AddSingleton(new SessionTelemetry(telemetry, reports, clock)));
        var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await WaitForAsync(() => clock.PendingTimers > 0, "the heartbeat to start waiting");
        clock.Advance(telemetry.Interval);
        await WaitForAsync(() => reports.Heartbeats.Count == 1, "the first heartbeat");

        await session.DisposeAsync();
        await WaitForAsync(() => clock.PendingTimers == 0, "the heartbeat loop to end");
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(telemetry.Interval);
            await Task.Delay(50, Ct);
        }

        Assert.Single(reports.Heartbeats);
    }

    private static Task WaitForAsync(Func<bool> condition, string what) =>
        TestWait.UntilAsync(condition, what, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(20));

    /// <summary>A clock whose one-shot timers fire only when the test moves it.</summary>
    private sealed class StepClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<StepTimer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public int PendingTimers
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count(t => t.Due is not null);
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new StepTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<StepTimer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due <= _now)];
                foreach (var timer in due)
                {
                    timer.Due = null;
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private DateTimeOffset? DueAt(TimeSpan dueTime)
        {
            lock (_gate)
            {
                return dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime;
            }
        }

        private void Remove(StepTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class StepTimer(StepClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = clock.DueAt(dueTime);
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                Due = null;
                clock.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingRendezvous : IRendezvousClient
    {
        public ConcurrentQueue<TelemetryReport> Heartbeats { get; } = new();

        public RendezvousState State => RendezvousState.Online;

        public DeviceId? DeviceId => null;

        public IPEndPoint? ReflectorEndPoint => null;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IAsyncEnumerable<IncomingConnection> ReadIncomingAsync(CancellationToken cancellationToken) =>
            Empty<IncomingConnection>();

        public IAsyncEnumerable<AdminCommand> ReadAdminCommandsAsync(CancellationToken cancellationToken) =>
            Empty<AdminCommand>();

        public Task<ConnectReady> RequestConnectionAsync(DeviceId target, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<DeviceId>?> QueryPresenceAsync(IReadOnlyList<DeviceId> deviceIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<DeviceId>?>(null);

        public Task AcceptAsync(string requestId, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RejectAsync(string requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ReportTelemetry(TelemetryReport report) => Heartbeats.Enqueue(report);

        public void ReportSessionEnded(SessionEndedReport report)
        {
        }

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
