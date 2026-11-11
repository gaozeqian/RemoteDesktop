using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.E2E.IntegrationTests;

/// <summary>Options for configuring an end-to-end device node.</summary>
public sealed class EndToEndNodeOptions
{
    public bool ForceRelay { get; set; }

    public string? FixedPassword { get; set; }

    public Permissions? AllowedPermissions { get; set; }

    public IReadOnlyList<IFeatureModule>? Features { get; set; }

    public Action<IServiceCollection>? ConfigureServices { get; set; }
}

/// <summary>
/// A complete, running end-to-end client/host node with its own isolated data directory, identity and services.
/// </summary>
public sealed class EndToEndNode : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _dataDirectory;

    private EndToEndNode(IHost host, string dataDirectory)
    {
        _host = host;
        _dataDirectory = dataDirectory;
    }

    public DeviceId Id => Rendezvous.DeviceId ?? throw new InvalidOperationException("Node not registered.");

    public IRendezvousClient Rendezvous => _host.Services.GetRequiredService<IRendezvousClient>();

    public HostCredentials Credentials => _host.Services.GetRequiredService<HostCredentials>();

    public ControllerSessionService Controller => _host.Services.GetRequiredService<ControllerSessionService>();

    public HostSessionService HostSessions => _host.Services.GetRequiredService<HostSessionService>();

    public AuthAttemptLimiter Limiter => _host.Services.GetRequiredService<AuthAttemptLimiter>();

    public IServiceProvider Services => _host.Services;

    public string DataDirectory => _dataDirectory;

    public Task<RemoteSession> ConnectAsync(
        DeviceId target,
        string password,
        SessionMode mode = SessionMode.Control,
        CancellationToken cancellationToken = default) =>
        Controller.ConnectAsync(target, password, mode, cancellationToken);

    public static async Task<EndToEndNode> StartAsync(
        Uri rendezvousUri,
        EndToEndNodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EndToEndNodeOptions();

        var dataDir = Path.Combine(Path.GetTempPath(), "rd-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        var secretStore = new InMemorySecretStore();
        if (!string.IsNullOrEmpty(options.FixedPassword))
        {
            FixedPasswordPolicy.Save(secretStore, options.FixedPassword);
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration.AddEnvironmentVariables();

        builder.Services
            .AddSingleton<ISecretStore>(secretStore)
            .AddQuicTransport(new TransportOptions
            {
                RendezvousUri = rendezvousUri,
                ForceRelay = options.ForceRelay,
                AllowInsecureSignaling = true,
            })
            .AddRemoteDesktopApplication();

        if (options.Features is not null)
        {
            foreach (var feature in options.Features)
            {
                builder.Services.AddSingleton<IFeatureModule>(feature);
            }
        }

        options.ConfigureServices?.Invoke(builder.Services);

        var host = builder.Build();
        var node = new EndToEndNode(host, dataDir);

        if (options.AllowedPermissions is { } permissions)
        {
            node.Credentials.AllowedPermissions = permissions;
        }

        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Rendezvous.StateChanged += (_, _) =>
        {
            if (node.Rendezvous.State == RendezvousState.Online)
            {
                online.TrySetResult();
            }
        };

        if (node.Rendezvous.State == RendezvousState.Online)
        {
            online.TrySetResult();
        }

        try
        {
            await host.StartAsync(cancellationToken);
            await online.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return node;
        }
        catch
        {
            // The caller never receives the instance, so nothing else would stop the host or remove the data folder
            // that this attempt already created.
            await node.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore teardown timeouts
        }
        finally
        {
            _host.Dispose();
            try
            {
                if (Directory.Exists(_dataDirectory))
                {
                    Directory.Delete(_dataDirectory, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

        public void Save(string name, ReadOnlySpan<byte> secret) => _secrets[name] = secret.ToArray();

        public byte[]? Load(string name) => _secrets.GetValueOrDefault(name);

        public void Delete(string name) => _secrets.TryRemove(name, out _);
    }
}
