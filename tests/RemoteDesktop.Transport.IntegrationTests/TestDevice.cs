using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.TestSupport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>A complete device (same composition as the real hosts, minus platform adapters) with its own identity.</summary>
public sealed class TestDevice : IAsyncDisposable
{
    private readonly IHost _host;

    private TestDevice(IHost host) => _host = host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DeviceId Id => Rendezvous.DeviceId ?? throw new InvalidOperationException("Not registered.");

    public IRendezvousClient Rendezvous => _host.Services.GetRequiredService<IRendezvousClient>();

    public HostCredentials Credentials => _host.Services.GetRequiredService<HostCredentials>();

    public ControllerSessionService Controller => _host.Services.GetRequiredService<ControllerSessionService>();

    public HostSessionService HostSessions => _host.Services.GetRequiredService<HostSessionService>();

    public AuthAttemptLimiter Limiter => _host.Services.GetRequiredService<AuthAttemptLimiter>();

    public static Task<TestDevice> StartAsync(ServerFixture server, bool forceRelay = false, params IFeatureModule[] modules) =>
        StartAsync(server.RendezvousUri, forceRelay, modules);

    public static Task<TestDevice> StartAsync(ServerFixture server, bool forceRelay, IInputAccess inputAccess, params IFeatureModule[] modules) =>
        StartAsync(server.RendezvousUri, forceRelay, inputAccess, modules);

    public static Task<TestDevice> StartAsync(Uri rendezvousUri, bool forceRelay = false, params IFeatureModule[] modules) =>
        StartAsync(rendezvousUri, forceRelay, inputAccess: null, modules);

    /// <summary>A device whose registrations the test may replace, after the standard composition has been added.</summary>
    public static Task<TestDevice> StartAsync(ServerFixture server, Action<IServiceCollection> configure) =>
        StartAsync(server.RendezvousUri, forceRelay: false, inputAccess: null, [], configure);

    public static Task<TestDevice> StartAsync(
        Uri rendezvousUri,
        bool forceRelay,
        IInputAccess? inputAccess,
        params IFeatureModule[] modules) =>
        StartAsync(rendezvousUri, forceRelay, inputAccess, modules, configure: null);

    private static async Task<TestDevice> StartAsync(
        Uri rendezvousUri,
        bool forceRelay,
        IInputAccess? inputAccess,
        IFeatureModule[] modules,
        Action<IServiceCollection>? configure)
    {
        var host = TestNodeHost.Build(
            new TestNodeHostOptions(rendezvousUri, forceRelay, AllowInsecureSignaling: false),
            new InMemorySecretStore(),
            services =>
            {
                foreach (var module in modules)
                {
                    services.AddSingleton(module);
                }

                if (inputAccess is not null)
                {
                    services.AddSingleton(inputAccess);
                }

                configure?.Invoke(services);
            });

        var device = new TestDevice(host);
        try
        {
            await TestNodeHost.StartAndWaitOnlineAsync(host, device.Rendezvous, TimeSpan.FromSeconds(10), Ct);
            return device;
        }
        catch
        {
            // The caller never receives the instance, so nothing else would ever stop the host this attempt built.
            await device.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _host.Dispose();
    }
}
