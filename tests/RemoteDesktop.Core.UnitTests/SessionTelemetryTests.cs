using System.Collections.Concurrent;
using System.Net;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The heartbeat of a hosted session: when the first one goes out, how often the rest do, when they stop, and what
/// they carry. Driven by a clock the test moves, so no interval costs any time.
/// </summary>
public sealed class SessionTelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_report_goes_out_five_seconds_in_and_the_rest_once_per_interval()
    {
        var rig = new Rig(intervalSeconds: 30);
        using var end = new CancellationTokenSource();
        rig.Telemetry.Track(Context(), end.Token);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(4.9));
        Assert.Empty(rig.Heartbeats);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(0.1));
        Assert.Single(rig.Heartbeats);

        // The interval is counted from the report, not from the start: 29.9 s later there is still only the first.
        await rig.AdvanceAsync(TimeSpan.FromSeconds(29.9));
        Assert.Single(rig.Heartbeats);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, rig.Heartbeats.Count);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, rig.Heartbeats.Count);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task An_interval_below_five_seconds_is_raised_to_five(int configured)
    {
        var options = new TelemetryOptions { IntervalSeconds = configured };
        Assert.Equal(TimeSpan.FromSeconds(5), options.Interval);

        var rig = new Rig(configured);
        using var end = new CancellationTokenSource();
        rig.Telemetry.Track(Context(), end.Token);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Single(rig.Heartbeats);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(4.9));
        Assert.Single(rig.Heartbeats);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, rig.Heartbeats.Count);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(6)]
    public async Task An_interval_longer_than_five_seconds_still_gets_its_first_report_at_five(int configured)
    {
        var rig = new Rig(configured);
        using var end = new CancellationTokenSource();
        rig.Telemetry.Track(Context(), end.Token);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.Single(rig.Heartbeats);
    }

    [Theory]
    [InlineData(3600)]
    [InlineData(100000)]
    [InlineData(int.MaxValue)]
    public async Task An_interval_above_an_hour_is_lowered_to_an_hour(int configured)
    {
        Assert.Equal(TimeSpan.FromHours(1), new TelemetryOptions { IntervalSeconds = configured }.Interval);

        var rig = new Rig(configured);
        using var end = new CancellationTokenSource();
        rig.Telemetry.Track(Context(), end.Token);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Single(rig.Heartbeats);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(3599));
        Assert.Single(rig.Heartbeats);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, rig.Heartbeats.Count);
    }

    [Fact]
    public void A_default_configuration_is_off_and_reports_every_thirty_seconds()
    {
        var options = new TelemetryOptions();

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Interval);
    }

    [Fact]
    public async Task Nothing_is_sent_and_no_timer_is_started_while_the_switch_is_off()
    {
        var rig = new Rig(intervalSeconds: 30, enabled: false);
        using var end = new CancellationTokenSource();

        rig.Telemetry.Track(Context(), end.Token);
        await rig.AdvanceAsync(TimeSpan.FromHours(2), settle: false);

        Assert.False(rig.Telemetry.Enabled);
        Assert.Equal(0, rig.Time.TimersCreated);
        Assert.Empty(rig.Heartbeats);
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_signaling_connection_even_when_the_switch_is_on()
    {
        var time = new SteppedTime();
        var telemetry = new SessionTelemetry(new TelemetryOptions { Enabled = true }, rendezvous: null, time);
        using var end = new CancellationTokenSource();

        telemetry.Track(Context(), end.Token);
        time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(50, Ct);

        Assert.False(telemetry.Enabled);
        Assert.Equal(0, time.TimersCreated);
    }

    [Fact]
    public async Task A_session_that_ends_stops_the_heartbeat_and_leaves_no_timer_behind()
    {
        var rig = new Rig(intervalSeconds: 5);
        using var end = new CancellationTokenSource();
        rig.Telemetry.Track(Context(), end.Token);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Single(rig.Heartbeats);

        await end.CancelAsync();
        await Rig.WaitUntilAsync(() => rig.Time.PendingTimers == 0);

        for (var i = 0; i < 3; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(5), settle: false);
        }

        Assert.Single(rig.Heartbeats);
    }

    [Fact]
    public async Task A_session_that_ends_before_the_first_report_never_reports()
    {
        var rig = new Rig(intervalSeconds: 30);
        using var end = new CancellationTokenSource();
        rig.Telemetry.Track(Context(), end.Token);
        await Rig.WaitUntilAsync(() => rig.Time.PendingTimers == 1);

        await end.CancelAsync();
        await Rig.WaitUntilAsync(() => rig.Time.PendingTimers == 0);
        await rig.AdvanceAsync(TimeSpan.FromMinutes(5), settle: false);

        Assert.Empty(rig.Heartbeats);
    }

    [Fact]
    public async Task A_session_that_is_already_over_when_tracking_starts_never_reports()
    {
        var rig = new Rig(intervalSeconds: 30);
        using var end = new CancellationTokenSource();
        await end.CancelAsync();

        rig.Telemetry.Track(Context(), end.Token);
        await rig.AdvanceAsync(TimeSpan.FromMinutes(5), settle: false);
        await Task.Delay(50, Ct);

        Assert.Empty(rig.Heartbeats);
    }

    [Fact]
    public async Task Every_session_has_its_own_heartbeat()
    {
        var rig = new Rig(intervalSeconds: 30);
        using var end = new CancellationTokenSource();
        var first = Context();
        var second = Context();
        rig.Telemetry.Track(first, end.Token);
        rig.Telemetry.Track(second, end.Token);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(5), loops: 2);

        Assert.Equal(
            new[] { first.Id.ToString(), second.Id.ToString() }.Order(),
            rig.Heartbeats.Select(h => h.SessionId).Order());
    }

    [Fact]
    public async Task A_host_report_names_the_session_the_mode_the_link_the_platform_and_the_version_and_carries_no_rtt()
    {
        var rig = new Rig(intervalSeconds: 30);
        using var end = new CancellationTokenSource();
        var context = Context(SessionMode.View, relaySessionId: "0f8fad5b-d9cb-469f-a165-70867728950e");
        rig.Telemetry.Track(context, end.Token);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));

        var report = Assert.Single(rig.Heartbeats);
        Assert.Equal(context.Id.ToString(), report.SessionId);
        Assert.Equal(TelemetryRole.Host, report.Role);
        Assert.Equal("view", report.Mode);
        Assert.Equal(TelemetryTransport.Relayed, report.Transport);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", report.RelaySessionId);
        Assert.Equal(ProductVersion.Full, report.Version);
        Assert.Equal(0u, report.RttMillis);
        Assert.Equal(
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            report.Platform);
    }

    [Fact]
    public async Task A_direct_control_session_reports_as_such_with_an_empty_relay_id()
    {
        var rig = new Rig(intervalSeconds: 30);
        using var end = new CancellationTokenSource();
        rig.Telemetry.Track(Context(SessionMode.Control, relaySessionId: null), end.Token);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));

        var report = Assert.Single(rig.Heartbeats);
        Assert.Equal("control", report.Mode);
        Assert.Equal(TelemetryTransport.Direct, report.Transport);
        Assert.Empty(report.RelaySessionId);
    }

    private static SessionContext Context(
        SessionMode mode = SessionMode.Control,
        string? relaySessionId = null) =>
        new(SessionId.New(), SessionRole.Host, new StubConnection(relaySessionId), AccessGrant.Create(mode, Permissions.All, Permissions.All));

    private sealed class Rig
    {
        private readonly RecordingRendezvous _rendezvous = new();

        public Rig(int intervalSeconds, bool enabled = true)
        {
            Time = new SteppedTime();
            Telemetry = new SessionTelemetry(
                new TelemetryOptions { Enabled = enabled, IntervalSeconds = intervalSeconds }, _rendezvous, Time);
        }

        public SteppedTime Time { get; }

        public SessionTelemetry Telemetry { get; }

        public IReadOnlyCollection<TelemetryReport> Heartbeats => [.. _rendezvous.Heartbeats];

        /// <summary>
        /// Moves the clock and, when <paramref name="settle"/> is set, waits until every loop has sent what was due and
        /// is waiting on a new timer again. A timer that fired is not pending until its loop arms the next one, and a
        /// report is sent before that happens, so the count is final once the timers are back.
        /// </summary>
        public async Task AdvanceAsync(TimeSpan by, bool settle = true, int loops = 1)
        {
            // The loops start on the thread pool: their first timer has to exist before the clock can reach it.
            if (settle)
            {
                await WaitUntilAsync(() => Time.PendingTimers >= loops);
            }

            Time.Advance(by);

            if (settle)
            {
                await WaitUntilAsync(() => Time.PendingTimers >= loops);
            }
        }

        public static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the heartbeat loop.");
                await Task.Delay(5, Ct);
            }
        }
    }

    private sealed class StubConnection(string? relaySessionId) : IPeerConnection
    {
        public DeviceId RemoteDevice => new(123456789);

        public ConnectionPath Path => relaySessionId is null ? ConnectionPath.Direct : ConnectionPath.Relayed;

        public string? RelaySessionId => relaySessionId;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
