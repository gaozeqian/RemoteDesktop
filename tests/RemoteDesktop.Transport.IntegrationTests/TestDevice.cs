using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
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

    public static Task<TestDevice> StartAsync(ServerFixture server, bool forceRelay = false, params IFeatureModule[] modules) =>
        StartAsync(server.RendezvousUri, forceRelay, modules);

    public static async Task<TestDevice> StartAsync(Uri rendezvousUri, bool forceRelay = false, params IFeatureModule[] modules)
    {
        // Deliberately not the full set of defaults. Host.CreateApplicationBuilder() adds appsettings.json as a
        // *reloading* file provider, and a reloading provider holds an inotify instance for as long as the host lives.
        // This project has no appsettings.json, so the watcher watches nothing — but these tests start hosts in
        // parallel, and enough of them at once exhausts the machine's user-wide limit of 128 inotify instances. The
        // suite then fails at host construction on any desktop that is already holding most of them, with an error
        // that says nothing about the code under test. TestRendezvous avoids this the same way, by using the slim
        // builder. What is kept here is everything the defaults would have provided except the watching file source:
        // the content root, and the environment.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddEnvironmentVariables();
        builder.Services
            .AddSingleton<ISecretStore, InMemorySecretStore>()
            .AddQuicTransport(new TransportOptions { RendezvousUri = rendezvousUri, ForceRelay = forceRelay })
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
