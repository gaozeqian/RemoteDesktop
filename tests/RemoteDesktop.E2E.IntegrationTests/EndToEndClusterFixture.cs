using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Relay;
using RemoteDesktop.Server.Shared;
using RemoteDesktop.Transport.IntegrationTests;

namespace RemoteDesktop.E2E.IntegrationTests;

/// <summary>
/// Orchestrates in-process cluster infrastructure for end-to-end testing:
/// a Rendezvous server (WebSocket signaling + UDP reflector) and a UDP Relay on loopback.
/// </summary>
public sealed class EndToEndClusterFixture : IAsyncLifetime
{
    private readonly CancellationTokenSource _stop = new();
    private WebApplication? _rendezvous;
    private Task? _relayLoop;

    public UdpRelay Relay { get; private set; } = null!;

    public Uri RendezvousUri { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Relay = new UdpRelay(
            new IPEndPoint(IPAddress.Loopback, 0),
            key,
            TimeSpan.FromMinutes(2),
            TimeProvider.System,
            new NoopOpsEventSink(),
            "relay-e2e-cluster",
            NullLogger<UdpRelay>.Instance);
        _relayLoop = Relay.RunAsync(_stop.Token);

        _rendezvous = await TestRendezvous.StartAsync(
            ("Rendezvous:RelayEndpoint", Relay.LocalEndPoint.ToString()),
            ("Rendezvous:RelaySharedKey", Convert.ToBase64String(key)),
            ("Rendezvous:RegistrationBurst", "1000000"),
            ("Rendezvous:MaxConnectionsPerAddress", "100000"),
            ("Rendezvous:MaxConnections", "100000"));

        RendezvousUri = TestRendezvous.SignalingUri(_rendezvous);
    }

    public Task<EndToEndNode> StartNodeAsync(
        EndToEndNodeOptions? options = null,
        CancellationToken cancellationToken = default) =>
        EndToEndNode.StartAsync(RendezvousUri, options, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_rendezvous is not null)
        {
            await _rendezvous.StopAsync();
            await _rendezvous.DisposeAsync();
        }

        if (_relayLoop is not null)
        {
            try
            {
                await _relayLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        Relay.Dispose();
        _stop.Dispose();
    }
}
