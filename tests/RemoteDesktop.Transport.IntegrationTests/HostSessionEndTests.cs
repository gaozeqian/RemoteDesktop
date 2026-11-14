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
/// A session ends on the host whichever side ends it. The host's own end report and its heartbeat both hang on the
/// session's end token, so that token has to be cancelled when the controller simply goes away, not only when the
/// person at the host presses Disconnect.
/// </summary>
public sealed class HostSessionEndTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_controller_that_closes_normally_makes_the_host_report_the_end_once_and_stop_its_heartbeat()
    {
        var reports = new RecordingRendezvous();
        var clock = new TimerClock();
        var telemetry = new TelemetryOptions { Enabled = true, IntervalSeconds = 5 };
        await using var host = await TestDevice.StartAsync(server, services => services
            .AddSingleton(new SessionTelemetry(telemetry, reports, clock))
            .AddSingleton(new SessionLifecycle(reports)));
        await using var controller = await TestDevice.StartAsync(server);
        var session = await controller.Controller.ConnectAsync(host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 1, "the session to appear on the host");

        // One heartbeat while the session is open: the clock moves by one interval once the loop is waiting on it.
        await WaitForAsync(() => clock.PendingTimers > 0, "the heartbeat to start waiting");
        clock.Advance(telemetry.Interval);
        await WaitForAsync(() => reports.Heartbeats.Count == 1, "the first heartbeat");
        Assert.Empty(reports.Ends);

        // The controller leaves the way it normally does; nobody presses Disconnect on the host.
        await session.DisposeAsync();
        await WaitForAsync(() => host.HostSessions.ActiveSessions.Count == 0, "the session to leave the host");

        await WaitForAsync(() => !reports.Ends.IsEmpty, "the host to report that the session ended");
        var end = Assert.Single(reports.Ends);
        Assert.Equal(TelemetryRole.Host, end.Role);

        // The heartbeat loop is over: more intervals pass and nothing more is sent.
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(telemetry.Interval);
            await Task.Delay(100, Ct);
        }

        Assert.Single(reports.Heartbeats);
        Assert.Single(reports.Ends);
    }

    private static Task WaitForAsync(Func<bool> condition, string what) =>
        TestWait.UntilAsync(condition, what, TimeSpan.FromSeconds(20));

    /// <summary>A clock whose timers only fire when the test moves it, so an interval costs no time.</summary>
    private sealed class TimerClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
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
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due <= _now)];
                foreach (var timer in due)
                {
                    timer.Due = null; // one-shot, which is all Task.Delay asks for
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

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(TimerClock clock, TimerCallback callback, object? state) : ITimer
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

    /// <summary>Keeps what the client would have queued for the server.</summary>
    private sealed class RecordingRendezvous : IRendezvousClient
    {
        public ConcurrentQueue<TelemetryReport> Heartbeats { get; } = new();

        public ConcurrentQueue<SessionEndedReport> Ends { get; } = new();

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

        public Task<ConnectReady> RequestConnectionAsync(
            DeviceId target,
            IEnumerable<PeerEndpoint> candidates,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlySet<DeviceId>?> QueryPresenceAsync(
            IReadOnlyList<DeviceId> deviceIds,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<DeviceId>?>(null);

        public Task AcceptAsync(string requestId, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RejectAsync(string requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ReportTelemetry(TelemetryReport report) => Heartbeats.Enqueue(report);

        public void ReportSessionEnded(SessionEndedReport report) => Ends.Enqueue(report);

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
