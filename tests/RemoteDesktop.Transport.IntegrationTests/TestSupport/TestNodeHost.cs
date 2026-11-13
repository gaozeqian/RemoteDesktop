using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.TestSupport;

/// <summary>What the two integration suites vary when they start a device.</summary>
internal sealed record TestNodeHostOptions(Uri RendezvousUri, bool ForceRelay, bool AllowInsecureSignaling);

/// <summary>
/// Builds a real device host — the same composition as the shipped hosts, minus the platform adapters — for the
/// transport and end-to-end suites. Kept in one file so the two node types cannot drift in how they start.
/// </summary>
internal static class TestNodeHost
{
    public static IHost Build(
        TestNodeHostOptions options,
        ISecretStore secretStore,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secretStore);

        // Deliberately not the full set of defaults. Host.CreateApplicationBuilder() adds appsettings.json as a
        // *reloading* file provider, and a reloading provider holds an inotify instance for as long as the host lives.
        // These projects have no appsettings.json, so the watcher watches nothing — but the tests start hosts in
        // parallel, and enough of them at once exhausts the machine's user-wide limit of 128 inotify instances. The
        // suite then fails at host construction with an error that says nothing about the code under test. What is
        // kept here is everything the defaults would have provided except the watching file source: the content root,
        // and the environment.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration.AddEnvironmentVariables();
        builder.Services
            .AddSingleton(secretStore)
            .AddQuicTransport(new TransportOptions
            {
                RendezvousUri = options.RendezvousUri,
                ForceRelay = options.ForceRelay,
                AllowInsecureSignaling = options.AllowInsecureSignaling,
            })
            .AddRemoteDesktopApplication();

        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }

    /// <summary>Starts the host and waits until the signaling connection is online.</summary>
    public static async Task StartAndWaitOnlineAsync(
        IHost host,
        IRendezvousClient rendezvous,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(rendezvous);

        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rendezvous.StateChanged += (_, _) =>
        {
            if (rendezvous.State == RendezvousState.Online)
            {
                online.TrySetResult();
            }
        };

        // A host restored from a previous connection may already be online before the handler runs.
        if (rendezvous.State == RendezvousState.Online)
        {
            online.TrySetResult();
        }

        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        await online.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A per-node secret store: no DPAPI, no keychain, no file, and no state shared between tests.</summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

    public void Save(string name, ReadOnlySpan<byte> secret) => _secrets[name] = secret.ToArray();

    public byte[]? Load(string name) => _secrets.GetValueOrDefault(name);

    public void Delete(string name) => _secrets.TryRemove(name, out _);
}
