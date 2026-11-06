using System.Collections.Concurrent;
using System.Net;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The end report is what gives a direct session a closing fact; it must be sent whether or not telemetry is on.
/// </summary>
public class SessionLifecycleTests
{
    private const string RelaySessionId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    [Fact]
    public void A_host_session_reports_its_end_when_the_session_token_is_cancelled()
    {
        var rendezvous = new RecordingRendezvous();
        var lifecycle = new SessionLifecycle(rendezvous);
        var id = SessionId.New();
        var context = Context(id, SessionRole.Host, RelaySessionId);

        using var end = new CancellationTokenSource();
        lifecycle.Track(context, end.Token);
        Assert.Empty(rendezvous.Ends);

        end.Cancel();

        var report = Assert.Single(rendezvous.Ends);
        Assert.Equal(id.ToString(), report.SessionId);
        Assert.Equal(RelaySessionId, report.RelaySessionId);
        Assert.Equal(TelemetryRole.Host, report.Role);
        Assert.Equal("closed", report.Reason);
    }

    [Fact]
    public void A_direct_session_reports_an_empty_relay_id_and_the_controller_role()
    {
        var rendezvous = new RecordingRendezvous();
        var lifecycle = new SessionLifecycle(rendezvous);
        var context = Context(SessionId.New(), SessionRole.Controller, relaySessionId: null);

        using var end = new CancellationTokenSource();
        lifecycle.Track(context, end.Token);
        end.Cancel();

        var report = Assert.Single(rendezvous.Ends);
        Assert.Empty(report.RelaySessionId);
        Assert.Equal(TelemetryRole.Controller, report.Role);
    }

    [Fact]
    public void A_session_whose_end_token_is_already_cancelled_is_reported_straight_away_and_only_once()
    {
        var rendezvous = new RecordingRendezvous();
        var lifecycle = new SessionLifecycle(rendezvous);
        using var end = new CancellationTokenSource();
        end.Cancel();

        lifecycle.Track(Context(SessionId.New(), SessionRole.Host, RelaySessionId), end.Token);

        Assert.Single(rendezvous.Ends);
    }

    [Fact]
    public void Two_sessions_are_reported_separately_under_their_own_ids()
    {
        var rendezvous = new RecordingRendezvous();
        var lifecycle = new SessionLifecycle(rendezvous);
        var first = SessionId.New();
        var second = SessionId.New();
        using var firstEnd = new CancellationTokenSource();
        using var secondEnd = new CancellationTokenSource();
        lifecycle.Track(Context(first, SessionRole.Host, RelaySessionId), firstEnd.Token);
        lifecycle.Track(Context(second, SessionRole.Host, RelaySessionId), secondEnd.Token);

        secondEnd.Cancel();

        Assert.Equal(second.ToString(), Assert.Single(rendezvous.Ends).SessionId);
        firstEnd.Cancel();
        Assert.Equal([second.ToString(), first.ToString()], rendezvous.Ends.Select(e => e.SessionId));
    }

    [Fact]
    public void A_composition_without_signaling_has_nothing_to_report()
    {
        var lifecycle = new SessionLifecycle(rendezvous: null);

        using var end = new CancellationTokenSource();
        lifecycle.Track(Context(SessionId.New(), SessionRole.Host, RelaySessionId), end.Token);

        // No rendezvous client, no queue, and no exception on a session's shutdown path.
        end.Cancel();
    }

    private static SessionContext Context(SessionId id, SessionRole role, string? relaySessionId) =>
        new(id, role, new StubConnection(relaySessionId), AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));

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

    /// <summary>Keeps what the client would have queued for the server.</summary>
    private sealed class RecordingRendezvous : IRendezvousClient
    {
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

        public void ReportTelemetry(TelemetryReport report)
        {
        }

        public void ReportSessionEnded(SessionEndedReport report) => Ends.Enqueue(report);

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
