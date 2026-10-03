using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>A complete device (same composition as the real hosts, minus platform adapters) with its own identity.</summary>
public sealed class TestDevice : IAsyncDisposable
{
    private readonly IHost _host;

    private TestDevice(IHost host) => _host = host;

    public DeviceId Id => Rendezvous.DeviceId ?? throw new InvalidOperationException("Not registered.");

    public IRendezvousClient Rendezvous => _host.Services.GetRequiredService<IRendezvousClient>();

    public HostCredentials Credentials => _host.Services.GetRequiredService<HostCredentials>();

    public ControllerSessionService Controller => _host.Services.GetRequiredService<ControllerSessionService>();

    public HostSessionService HostSessions => _host.Services.GetRequiredService<HostSessionService>();

    public static async Task<TestDevice> StartAsync(ServerFixture server, bool forceRelay = false, params IFeatureModule[] modules)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services
            .AddSingleton<ISecretStore, InMemorySecretStore>()
            .AddQuicTransport(new TransportOptions { RendezvousUri = server.RendezvousUri, ForceRelay = forceRelay })
            .AddRemoteDesktopApplication();
        foreach (var module in modules)
        {
            builder.Services.AddSingleton(module);
        }

        var device = new TestDevice(builder.Build());
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Rendezvous.StateChanged += (_, _) =>
        {
            if (device.Rendezvous.State == RendezvousState.Online)
            {
                online.TrySetResult();
            }
        };

        await device._host.StartAsync();
        await online.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return device;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _host.Dispose();
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _secrets = [];

        public void Save(string name, ReadOnlySpan<byte> secret) => _secrets[name] = secret.ToArray();

        public byte[]? Load(string name) => _secrets.GetValueOrDefault(name);

        public void Delete(string name) => _secrets.TryRemove(name, out _);
    }
}
