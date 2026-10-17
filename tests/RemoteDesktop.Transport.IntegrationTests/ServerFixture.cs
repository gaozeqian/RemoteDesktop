using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Relay;
using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>Real rendezvous (Kestrel + WebSocket + UDP reflector) and relay on loopback, shared by all tests.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private readonly CancellationTokenSource _stop = new();
    private WebApplication? _rendezvous;
    private Task? _relayLoop;

    public UdpRelay Relay { get; private set; } = null!;

    public Uri RendezvousUri { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Relay = new UdpRelay(new IPEndPoint(IPAddress.Loopback, 0), key, TimeSpan.FromMinutes(2), TimeProvider.System, new NoopOpsEventSink(), "relay-test", NullLogger<UdpRelay>.Instance);
        _relayLoop = Relay.RunAsync(_stop.Token);

        // The address limits are sized for a real site and every test device here is a new identity from loopback,
        // so the shared fixture lifts them out of the way: the limits themselves have their own tests, with servers
        // of their own (RendezvousLimitTests).
        _rendezvous = await TestRendezvous.StartAsync(
            ("Rendezvous:RelayEndpoint", Relay.LocalEndPoint.ToString()),
            ("Rendezvous:RelaySharedKey", Convert.ToBase64String(key)),
            ("Rendezvous:RegistrationBurst", "1000000"),
            ("Rendezvous:MaxConnectionsPerAddress", "100000"),
            ("Rendezvous:MaxConnections", "100000"));
        RendezvousUri = TestRendezvous.SignalingUri(_rendezvous);
    }

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
            await _relayLoop;
        }

        Relay.Dispose();
        _stop.Dispose();
    }
}
