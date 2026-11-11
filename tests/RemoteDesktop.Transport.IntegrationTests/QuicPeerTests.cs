using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Domain;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Identity;
using RemoteDesktop.Transport.Nat;
using RemoteDesktop.Transport.Quic;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The QUIC listener and connector on their own, between a scripted rendezvous client and real loopback sockets: the
/// pinned mutual TLS, the application protocol, how many candidates are used and how many connect requests the host
/// prepares at once.
/// </summary>
public sealed class QuicPeerTests
{
    private const uint ControllerDeviceId = 123456789;
    private static readonly DeviceId Controller = new((int)ControllerDeviceId);
    private static readonly DeviceId Host = new(987654321);
    private static readonly SslApplicationProtocol Alpn = new("rd/1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TransportOptions Options(
        bool forceRelay = false,
        double udpSeconds = 1.5,
        double directSeconds = 3,
        double hostAcceptSeconds = 3) => new()
    {
        ForceRelay = forceRelay,
        UdpRequestTimeout = TimeSpan.FromSeconds(udpSeconds),
        DirectAttemptTimeout = TimeSpan.FromSeconds(directSeconds),
        HostAcceptTimeout = TimeSpan.FromSeconds(hostAcceptSeconds),
    };

    private static void RequireQuic() =>
        Assert.SkipUnless(QuicListener.IsSupported && QuicConnection.IsSupported, "QUIC is not available on this machine.");

    // ------------------------------------------------------------------ the pinned mutual TLS

    [Fact]
    public async Task A_controller_holding_the_pinned_certificate_connects_and_each_end_learns_the_others_fingerprint()
    {
        RequireQuic();
        using var hostIdentity = DeviceIdentity.CreateEphemeral();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        var rendezvous = new ScriptedRendezvous();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new QuicPeerListener(rendezvous, hostIdentity, Options(), NullLogger<QuicPeerListener>.Instance);
        var running = listener.RunAsync(stop.Token);
        rendezvous.Incoming.Writer.TryWrite(Request("req-1", controllerIdentity.Fingerprint));
        var hostCandidates = await rendezvous.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        rendezvous.OnRequest = _ => Ready(hostIdentity.Fingerprint, hostCandidates);
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(), NullLogger<QuicPeerConnector>.Instance);

        await using var client = await connector.ConnectAsync(Host, Ct);
        await using var server = await listener.AcceptAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ConnectionPath.Direct, client.Path);
        Assert.Equal(Host, client.RemoteDevice);
        Assert.Equal(controllerIdentity.Fingerprint, client.LocalFingerprint);
        Assert.Equal(hostIdentity.Fingerprint, client.RemoteFingerprint);
        Assert.Equal(ConnectionPath.Direct, server.Path);
        Assert.Equal(Controller, server.RemoteDevice);
        Assert.Equal(hostIdentity.Fingerprint, server.LocalFingerprint);
        Assert.Equal(controllerIdentity.Fingerprint, server.RemoteFingerprint);

        await StopAsync(stop, running);
    }

    [Fact]
    public async Task The_connector_offers_rd_1_and_its_own_certificate_and_only_trusts_the_pinned_server_certificate()
    {
        RequireQuic();
        using var hostIdentity = DeviceIdentity.CreateEphemeral();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        await using var raw = await RawServer.StartAsync(hostIdentity, [Alpn]);
        var rendezvous = new ScriptedRendezvous
        {
            OnRequest = _ => Ready(hostIdentity.Fingerprint, [Candidate(raw.EndPoint)]),
        };
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(), NullLogger<QuicPeerConnector>.Instance);

        var accepting = raw.AcceptAsync(Ct);
        await using var client = await connector.ConnectAsync(Host, Ct);
        await using var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(Alpn, accepted.NegotiatedApplicationProtocol);

        // The server asked for a client certificate; what it was shown is the controller's identity.
        Assert.NotNull(accepted.RemoteCertificate);
        using var shown = new X509Certificate2(accepted.RemoteCertificate);
        Assert.Equal(controllerIdentity.Fingerprint, DeviceIdentity.ComputeFingerprint(shown));
    }

    [Fact]
    public async Task A_server_certificate_other_than_the_one_the_rendezvous_named_is_refused_by_the_controller()
    {
        RequireQuic();
        using var hostIdentity = DeviceIdentity.CreateEphemeral();
        using var impostor = DeviceIdentity.CreateEphemeral();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        await using var raw = await RawServer.StartAsync(impostor, [Alpn]);
        var rendezvous = new ScriptedRendezvous
        {
            // The server vouches for hostIdentity; the thing answering holds another certificate.
            OnRequest = _ => Ready(hostIdentity.Fingerprint, [Candidate(raw.EndPoint)]),
        };
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(), NullLogger<QuicPeerConnector>.Instance);

        var failure = await Assert.ThrowsAsync<PeerConnectException>(() => connector.ConnectAsync(Host, Ct));

        Assert.Equal(PeerConnectFailure.NoPath, failure.Failure);
        Assert.Contains($"direct {raw.EndPoint}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fingerprint_comparison_ignores_letter_case()
    {
        RequireQuic();
        using var hostIdentity = DeviceIdentity.CreateEphemeral();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        await using var raw = await RawServer.StartAsync(hostIdentity, [Alpn]);
        var rendezvous = new ScriptedRendezvous
        {
            OnRequest = _ => Ready(hostIdentity.Fingerprint.ToLowerInvariant(), [Candidate(raw.EndPoint)]),
        };
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(), NullLogger<QuicPeerConnector>.Instance);

        var accepting = raw.AcceptAsync(Ct);
        await using var client = await connector.ConnectAsync(Host, Ct);
        await using var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ConnectionPath.Direct, client.Path);
    }

    [Fact]
    public async Task A_client_certificate_other_than_the_pinned_controller_is_turned_away_and_the_host_keeps_waiting()
    {
        RequireQuic();
        using var hostIdentity = DeviceIdentity.CreateEphemeral();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        using var impostor = DeviceIdentity.CreateEphemeral();
        var rendezvous = new ScriptedRendezvous();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new QuicPeerListener(rendezvous, hostIdentity, Options(), NullLogger<QuicPeerListener>.Instance);
        var running = listener.RunAsync(stop.Token);
        rendezvous.Incoming.Writer.TryWrite(Request("req-1", controllerIdentity.Fingerprint));
        var hostCandidates = await rendezvous.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var endPoint = ToEndPoint(hostCandidates[^1]);

        await TryConnectRawAsync(endPoint, impostor, [Alpn]);

        // Nothing is handed to the session service for the impostor...
        await AssertNothingAcceptedAsync(listener, TimeSpan.FromMilliseconds(800));

        // ...and the real controller, arriving afterwards, is still let in by the same listener.
        await using var raw = await QuicConnection.ConnectAsync(RawClientOptions(endPoint, controllerIdentity, [Alpn]), Ct);
        await using var server = await listener.AcceptAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(controllerIdentity.Fingerprint, server.RemoteFingerprint);
        Assert.Equal(Alpn, raw.NegotiatedApplicationProtocol);

        await StopAsync(stop, running);
    }

    [Fact]
    public async Task A_client_that_asks_for_another_application_protocol_is_refused()
    {
        RequireQuic();
        using var hostIdentity = DeviceIdentity.CreateEphemeral();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        var rendezvous = new ScriptedRendezvous();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new QuicPeerListener(rendezvous, hostIdentity, Options(), NullLogger<QuicPeerListener>.Instance);
        var running = listener.RunAsync(stop.Token);
        rendezvous.Incoming.Writer.TryWrite(Request("req-1", controllerIdentity.Fingerprint));
        var endPoint = ToEndPoint((await rendezvous.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct))[^1]);

        // Even with the pinned certificate: the protocol is part of what the host agrees to speak.
        await Assert.ThrowsAnyAsync<AuthenticationException>(async () =>
        {
            await using var refused = await QuicConnection.ConnectAsync(RawClientOptions(endPoint, controllerIdentity, [new SslApplicationProtocol("rd/2")]), Ct);
        });

        await AssertNothingAcceptedAsync(listener, TimeSpan.FromMilliseconds(500));

        await StopAsync(stop, running);
    }

    // ------------------------------------------------------------------ candidate limit

    [Fact]
    public async Task The_host_punches_towards_at_most_the_candidate_limit_of_the_controllers_candidates()
    {
        RequireQuic();
        using var hostIdentity = DeviceIdentity.CreateEphemeral();
        using var peers = new UdpPeers(CandidateGatherer.MaxPeerCandidates + 6);
        var rendezvous = new ScriptedRendezvous();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new QuicPeerListener(rendezvous, hostIdentity, Options(), NullLogger<QuicPeerListener>.Instance);
        var running = listener.RunAsync(stop.Token);
        var incoming = Request("req-1", "00");
        incoming.ControllerCandidates.AddRange(peers.EndPoints.Select(CandidateGatherer.ToMessage));
        rendezvous.Incoming.Writer.TryWrite(incoming);

        // The host tells the server it is ready only after it has punched.
        await rendezvous.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(200, Ct);

        for (var i = 0; i < CandidateGatherer.MaxPeerCandidates; i++)
        {
            Assert.Equal(3, peers.PunchPacketsAt(i));
        }

        for (var i = CandidateGatherer.MaxPeerCandidates; i < peers.EndPoints.Count; i++)
        {
            Assert.Equal(0, peers.PunchPacketsAt(i));
        }

        await StopAsync(stop, running);
    }

    [Fact]
    public async Task The_controller_punches_towards_at_most_the_candidate_limit_of_the_hosts_candidates()
    {
        RequireQuic();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        using var peers = new UdpPeers(CandidateGatherer.MaxPeerCandidates + 6);
        var rendezvous = new ScriptedRendezvous
        {
            OnRequest = _ => Ready("00", peers.EndPoints.Select(CandidateGatherer.ToMessage)),
        };
        // Long enough that the first direct attempt is still running when the punching is judged.
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(directSeconds: 60), NullLogger<QuicPeerConnector>.Instance);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var connecting = connector.ConnectAsync(Host, cancel.Token);
        await peers.WaitUntilPunchedAsync(CandidateGatherer.MaxPeerCandidates, Ct);
        await Task.Delay(200, Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);

        for (var i = CandidateGatherer.MaxPeerCandidates; i < peers.EndPoints.Count; i++)
        {
            Assert.Equal(0, peers.PunchPacketsAt(i));
        }
    }

    // ------------------------------------------------------------------ requests prepared at once

    [Fact]
    public async Task The_host_prepares_at_most_thirty_two_connect_requests_at_once_and_drops_the_rest()
    {
        RequireQuic();
        using var hostIdentity = DeviceIdentity.CreateEphemeral();
        using var reflector = new Reflector();
        var rendezvous = new ScriptedRendezvous { ReflectorEndPoint = reflector.EndPoint };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // The reflector stays silent, so every request that is taken on stays in preparation for the whole UDP timeout.
        var listener = new QuicPeerListener(rendezvous, hostIdentity, Options(udpSeconds: 60, hostAcceptSeconds: 0.5), NullLogger<QuicPeerListener>.Instance);
        var running = listener.RunAsync(stop.Token);
        for (var i = 0; i < 40; i++)
        {
            rendezvous.Incoming.Writer.TryWrite(Request($"req-{i}", "00"));
        }

        await reflector.WaitForTransactionsAsync(32, Ct);

        // Several retransmission intervals later nothing beyond the limit has started.
        await Task.Delay(700, Ct);
        Assert.Equal(32, reflector.Transactions.Count);

        // One request finishing frees one place, and a request that arrives afterwards takes it.
        reflector.Answer(reflector.Transactions[0]);
        await rendezvous.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        rendezvous.Incoming.Writer.TryWrite(Request("req-late", "00"));
        await reflector.WaitForTransactionsAsync(33, Ct);
        await Task.Delay(500, Ct);
        Assert.Equal(33, reflector.Transactions.Count);

        await StopAsync(stop, running);
    }

    [Fact]
    public async Task A_listener_that_does_not_accept_incoming_connections_ignores_requests()
    {
        using var hostIdentity = DeviceIdentity.CreateEphemeral();
        var rendezvous = new ScriptedRendezvous();
        var options = Options();
        options.AcceptIncoming = false;
        var listener = new QuicPeerListener(rendezvous, hostIdentity, options, NullLogger<QuicPeerListener>.Instance);
        rendezvous.Incoming.Writer.TryWrite(Request("req-1", "00"));

        await listener.RunAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.False(rendezvous.Accepted.Task.IsCompleted);
    }

    // ------------------------------------------------------------------ what the connector reports when nothing works

    [Fact]
    public async Task The_connector_falls_through_to_the_relay_when_every_direct_candidate_fails()
    {
        RequireQuic();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        using var deadDirect = new UdpPeers(1);
        using var silentRelay = new Reflector();
        var rendezvous = new ScriptedRendezvous
        {
            OnRequest = _ =>
            {
                var ready = ReadyWithRelay("127.0.0.1", (uint)silentRelay.EndPoint.Port);
                ready.HostCandidates.AddRange(deadDirect.EndPoints.Select(Candidate));
                return ready;
            },
        };
        var connector = new QuicPeerConnector(
            rendezvous,
            controllerIdentity,
            Options(directSeconds: 0.2, udpSeconds: 0.2),
            NullLogger<QuicPeerConnector>.Instance);

        var failure = await Assert.ThrowsAsync<PeerConnectException>(() => connector.ConnectAsync(Host, Ct));

        // ForceRelay would skip the direct loop entirely. Together these facts prove the connector walked the
        // failing direct candidates and then fell through to the relay attempt, instead of giving up on the direct
        // path or treating "no relay forced" as "no relay allowed".
        Assert.NotEmpty(rendezvous.RequestedCandidates!);
        Assert.Contains($"direct {deadDirect.EndPoints[0]}", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"relay {silentRelay.EndPoint} did not acknowledge the bind", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_reflector_no_candidates_and_no_relay_the_failure_lists_each_missing_path()
    {
        RequireQuic();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        var rendezvous = new ScriptedRendezvous { OnRequest = _ => Ready("00", []) };
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(), NullLogger<QuicPeerConnector>.Instance);

        var failure = await Assert.ThrowsAsync<PeerConnectException>(() => connector.ConnectAsync(Host, Ct));

        Assert.Equal(PeerConnectFailure.NoPath, failure.Failure);
        Assert.Contains("the reflector did not report a public endpoint", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the device offered no direct candidate", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the server offered no relay", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forced_relay_offers_no_candidates_and_skips_the_direct_attempts()
    {
        RequireQuic();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        using var peers = new UdpPeers(1);
        var rendezvous = new ScriptedRendezvous
        {
            // Direct candidates exist, and must be ignored.
            OnRequest = _ => Ready("00", peers.EndPoints.Select(CandidateGatherer.ToMessage)),
        };
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(forceRelay: true), NullLogger<QuicPeerConnector>.Instance);

        var failure = await Assert.ThrowsAsync<PeerConnectException>(() => connector.ConnectAsync(Host, Ct));

        Assert.Empty(rendezvous.RequestedCandidates!);
        Assert.Contains("direct attempts were skipped (the relay was forced)", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, peers.PunchPacketsAt(0));
    }

    [Fact]
    public async Task A_relay_with_a_nonsensical_port_is_treated_as_no_relay_rather_than_throwing_from_the_endpoint()
    {
        RequireQuic();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        foreach (var port in new uint[] { 0, 70000 })
        {
            var rendezvous = new ScriptedRendezvous { OnRequest = _ => ReadyWithRelay("127.0.0.1", port) };
            var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(), NullLogger<QuicPeerConnector>.Instance);

            var failure = await Assert.ThrowsAsync<PeerConnectException>(() => connector.ConnectAsync(Host, Ct));

            Assert.Equal(PeerConnectFailure.NoPath, failure.Failure);
            Assert.Contains($"the server offered relay port {port}", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_relay_address_that_is_not_an_address_is_treated_as_no_relay()
    {
        RequireQuic();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        var rendezvous = new ScriptedRendezvous { OnRequest = _ => ReadyWithRelay("relay.example", 3478) };
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(), NullLogger<QuicPeerConnector>.Instance);

        var failure = await Assert.ThrowsAsync<PeerConnectException>(() => connector.ConnectAsync(Host, Ct));

        Assert.Contains("the server offered no relay", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_relay_that_does_not_acknowledge_the_bind_is_reported_as_such()
    {
        RequireQuic();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        using var silentRelay = new Reflector();
        var rendezvous = new ScriptedRendezvous { OnRequest = _ => ReadyWithRelay("127.0.0.1", (uint)silentRelay.EndPoint.Port) };
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(udpSeconds: 0.2), NullLogger<QuicPeerConnector>.Instance);

        var failure = await Assert.ThrowsAsync<PeerConnectException>(() => connector.ConnectAsync(Host, Ct));

        Assert.Contains($"relay {silentRelay.EndPoint} did not acknowledge the bind", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reflexive_endpoint_is_offered_to_the_server_and_named_in_the_failure()
    {
        RequireQuic();
        using var controllerIdentity = DeviceIdentity.CreateEphemeral();
        using var reflector = new Reflector();
        var observed = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 4000);
        reflector.AnswerEverythingWith(observed);
        var rendezvous = new ScriptedRendezvous
        {
            ReflectorEndPoint = reflector.EndPoint,
            OnRequest = _ => Ready("00", []),
        };
        var connector = new QuicPeerConnector(rendezvous, controllerIdentity, Options(), NullLogger<QuicPeerConnector>.Instance);

        var failure = await Assert.ThrowsAsync<PeerConnectException>(() => connector.ConnectAsync(Host, Ct));

        Assert.Contains("the reflector saw us at 203.0.113.9:4000", failure.Message, StringComparison.Ordinal);
        var offered = rendezvous.RequestedCandidates!.ToList();
        Assert.Contains(offered, c => c.Address == "203.0.113.9" && c.Port == 4000);

        // Loopback is last, so that two devices on one machine can always find each other.
        Assert.Equal("127.0.0.1", offered[^1].Address);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The listener's run loop ends by cancellation, which is how the host service stops it.</summary>
    /// <remarks>
    /// The bound is waited on with <see cref="CancellationToken.None"/> on purpose: passing the test's token would make
    /// the wait itself throw the very <see cref="OperationCanceledException"/> this test is asserting the loop throws,
    /// and the assertion would then hold no matter how the loop ended.
    /// </remarks>
    private static async Task StopAsync(CancellationTokenSource stop, Task running)
    {
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    /// <summary>Waits and expects the session queue to stay empty; the read is cancelled so it cannot swallow a later item.</summary>
    private static async Task AssertNothingAcceptedAsync(QuicPeerListener listener, TimeSpan wait)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(wait);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await listener.AcceptAsync(cancel.Token));
    }

    private static IncomingConnection Request(string requestId, string controllerFingerprint) => new()
    {
        RequestId = requestId,
        ControllerDeviceId = ControllerDeviceId,
        ControllerFingerprint = controllerFingerprint,
        SessionId = Guid.NewGuid().ToString("D"),
    };

    private static ConnectReady Ready(string hostFingerprint, IEnumerable<PeerEndpoint> hostCandidates)
    {
        var ready = new ConnectReady { RequestId = "r", HostFingerprint = hostFingerprint, SessionId = Guid.NewGuid().ToString("D") };
        ready.HostCandidates.AddRange(hostCandidates);
        return ready;
    }

    private static ConnectReady ReadyWithRelay(string address, uint port)
    {
        var ready = Ready("00", []);
        ready.Relay = new RelayInfo
        {
            Endpoint = new PeerEndpoint { Address = address, Port = port },
            Ticket = ByteString.CopyFrom(1, 2, 3),
        };
        return ready;
    }

    private static PeerEndpoint Candidate(IPEndPoint endPoint) => CandidateGatherer.ToMessage(endPoint);

    private static IPEndPoint ToEndPoint(PeerEndpoint endpoint) => new(IPAddress.Parse(endpoint.Address), (int)endpoint.Port);

    private static QuicClientConnectionOptions RawClientOptions(IPEndPoint remote, DeviceIdentity identity, SslApplicationProtocol[] protocols) => new()
    {
        RemoteEndPoint = remote,
        DefaultStreamErrorCode = 1,
        DefaultCloseErrorCode = 0,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [.. protocols],
            TargetHost = "remotedesktop.peer",
            ClientCertificates = [identity.Certificate],
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
        },
    };

    /// <summary>
    /// Connects the way a stranger would and gives up quietly: depending on when the host's certificate check runs,
    /// the client either fails to connect or connects and is closed straight away.
    /// </summary>
    private static async Task TryConnectRawAsync(IPEndPoint remote, DeviceIdentity identity, SslApplicationProtocol[] protocols)
    {
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(3));
            await using var connection = await QuicConnection.ConnectAsync(RawClientOptions(remote, identity, protocols), attempt.Token);
            await Task.Delay(300, Ct);
        }
        catch (Exception ex) when (ex is QuicException or AuthenticationException or OperationCanceledException)
        {
            // Turned away, which is what the caller is checking the host's side of.
        }
    }

    /// <summary>The rendezvous client as the two QUIC classes see it: scripted, in memory.</summary>
    private sealed class ScriptedRendezvous : IRendezvousClient
    {
        private readonly TaskCompletionSource<List<PeerEndpoint>> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<IncomingConnection> Incoming { get; } = Channel.CreateUnbounded<IncomingConnection>();

        public TaskCompletionSource<List<PeerEndpoint>> Accepted => _accepted;

        public Func<IReadOnlyList<PeerEndpoint>, ConnectReady>? OnRequest { get; set; }

        public IReadOnlyList<PeerEndpoint>? RequestedCandidates { get; private set; }

        public RendezvousState State => RendezvousState.Online;

        public DeviceId? DeviceId => null;

        public IPEndPoint? ReflectorEndPoint { get; set; }

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IAsyncEnumerable<IncomingConnection> ReadIncomingAsync(CancellationToken cancellationToken) =>
            Incoming.Reader.ReadAllAsync(cancellationToken);

        public IAsyncEnumerable<AdminCommand> ReadAdminCommandsAsync(CancellationToken cancellationToken) =>
            Empty<AdminCommand>();

        public Task<ConnectReady> RequestConnectionAsync(DeviceId target, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken)
        {
            var list = candidates.ToList();
            RequestedCandidates = list;
            return Task.FromResult((OnRequest ?? throw new InvalidOperationException("No script."))(list));
        }

        public Task<IReadOnlySet<DeviceId>?> QueryPresenceAsync(IReadOnlyList<DeviceId> deviceIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<DeviceId>?>(null);

        public Task AcceptAsync(string requestId, IEnumerable<PeerEndpoint> candidates, CancellationToken cancellationToken)
        {
            _accepted.TrySetResult(candidates.ToList());
            return Task.CompletedTask;
        }

        public Task RejectAsync(string requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ReportTelemetry(TelemetryReport report)
        {
        }

        public void ReportSessionEnded(SessionEndedReport report)
        {
        }

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>A QUIC server with the host's certificate and no opinion about who connects.</summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly QuicListener _listener;

        private RawServer(QuicListener listener) => _listener = listener;

        public IPEndPoint EndPoint => _listener.LocalEndPoint;

        public static async Task<RawServer> StartAsync(DeviceIdentity identity, SslApplicationProtocol[] protocols)
        {
            var listener = await QuicListener.ListenAsync(
                new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                    ApplicationProtocols = [.. protocols],
                    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                    {
                        DefaultStreamErrorCode = 1,
                        DefaultCloseErrorCode = 0,
                        ServerAuthenticationOptions = new SslServerAuthenticationOptions
                        {
                            ApplicationProtocols = [.. protocols],
                            ServerCertificate = identity.Certificate,
                            ClientCertificateRequired = true,
                            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
                        },
                    }),
                },
                Ct);
            return new RawServer(listener);
        }

        public async Task<QuicConnection> AcceptAsync(CancellationToken cancellationToken) =>
            await _listener.AcceptConnectionAsync(cancellationToken);

        public ValueTask DisposeAsync() => _listener.DisposeAsync();
    }

    /// <summary>Loopback UDP sockets that count the punch packets that reach them.</summary>
    private sealed class UdpPeers : IDisposable
    {
        private readonly List<Socket> _sockets = [];
        private readonly int[] _punches;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _loops = [];

        public UdpPeers(int count)
        {
            _punches = new int[count];
            for (var i = 0; i < count; i++)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows())
                {
                    socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                }

                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _sockets.Add(socket);
                var index = i;
                _loops.Add(Task.Run(() => ReceiveLoopAsync(socket, index)));
            }

            EndPoints = [.. _sockets.Select(s => (IPEndPoint)s.LocalEndPoint!)];
        }

        public IReadOnlyList<IPEndPoint> EndPoints { get; }

        public int PunchPacketsAt(int index) => Volatile.Read(ref _punches[index]);

        public async Task WaitUntilPunchedAsync(int firstCount, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (Enumerable.Range(0, firstCount).Any(i => PunchPacketsAt(i) < 3))
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }

            try
            {
                Task.WaitAll([.. _loops], TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The loops end with their sockets.
            }

            _stop.Dispose();
        }

        private async Task ReceiveLoopAsync(Socket socket, int index)
        {
            var buffer = new byte[1500];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
                    if (UdpControlPacket.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var type, out _) && type == UdpControlPacketType.Punch)
                    {
                        Interlocked.Increment(ref _punches[index]);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>A UDP endpoint that records reflect requests and answers only when told to.</summary>
    private sealed class Reflector : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<string, (byte[] Id, EndPoint From)> _seen = [];
        private readonly List<string> _order = [];
        private readonly Task _loop;
        private IPEndPoint? _answerAll;

        public Reflector()
        {
            if (OperatingSystem.IsWindows())
            {
                _socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            }

            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _loop = Task.Run(ReceiveLoopAsync);
        }

        public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

        /// <summary>The distinct transaction ids seen, in order of first appearance.</summary>
        public IReadOnlyList<string> Transactions
        {
            get
            {
                lock (_seen)
                {
                    return [.. _order];
                }
            }
        }

        public void AnswerEverythingWith(IPEndPoint observed) => _answerAll = observed;

        public void Answer(string transaction)
        {
            (byte[] id, EndPoint from) entry;
            lock (_seen)
            {
                entry = _seen[transaction];
            }

            _socket.SendTo(UdpControlPacket.CreateReflectResponse(entry.id, new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1000)), entry.from);
        }

        public async Task WaitForTransactionsAsync(int count, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (Transactions.Count < count)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The loop ends with the socket.
            }

            _stop.Dispose();
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[1500];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
                    if (!UdpControlPacket.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var type, out var body) ||
                        type != UdpControlPacketType.ReflectRequest ||
                        body.Length < UdpControlPacket.TransactionIdSize)
                    {
                        continue;
                    }

                    var id = body[..UdpControlPacket.TransactionIdSize].ToArray();
                    var key = Convert.ToHexString(id);
                    lock (_seen)
                    {
                        if (_seen.TryAdd(key, (id, received.RemoteEndPoint)))
                        {
                            _order.Add(key);
                        }
                    }

                    if (_answerAll is { } observed)
                    {
                        _socket.SendTo(UdpControlPacket.CreateReflectResponse(id, observed), received.RemoteEndPoint);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        }
    }
}
