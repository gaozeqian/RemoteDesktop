using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Relay;
using RemoteDesktop.Server.Rendezvous;

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
        Relay = new UdpRelay(new IPEndPoint(IPAddress.Loopback, 0), key, TimeSpan.FromMinutes(2), TimeProvider.System, NullLogger<UdpRelay>.Instance);
        _relayLoop = Relay.RunAsync(_stop.Token);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Rendezvous:ReflectorPort"] = "0",
            ["Rendezvous:RelayEndpoint"] = Relay.LocalEndPoint.ToString(),
            ["Rendezvous:RelaySharedKey"] = Convert.ToBase64String(key),
        });
        builder.Services.AddRendezvous(builder.Configuration);

        _rendezvous = builder.Build();
        _rendezvous.MapRendezvous();
        await _rendezvous.StartAsync();

        var address = new Uri(_rendezvous.Urls.First());
        RendezvousUri = new Uri($"ws://127.0.0.1:{address.Port}{RendezvousServer.SignalingPath}");
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
