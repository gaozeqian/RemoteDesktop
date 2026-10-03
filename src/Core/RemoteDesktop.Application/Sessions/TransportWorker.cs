using Microsoft.Extensions.Hosting;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Application.Sessions;

/// <summary>Keeps the device online at the rendezvous server and ready for incoming connections.</summary>
public sealed class TransportWorker(IRendezvousClient rendezvous, IPeerListener listener) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(rendezvous.RunAsync(stoppingToken), listener.RunAsync(stoppingToken));
}
