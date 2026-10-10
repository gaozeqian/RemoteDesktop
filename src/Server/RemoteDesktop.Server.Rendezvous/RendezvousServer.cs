using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>Composition of the rendezvous server, shared by <c>Program</c> and the integration tests.</summary>
public static class RendezvousServer
{
    public const string SignalingPath = "/ws";

    /// <summary>Mapped only when <see cref="RendezvousOptions.MetricsEnabled"/> is set; off, it is a 404.</summary>
    public const string MetricsPath = "/metrics";

    public static IServiceCollection AddRendezvous(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RendezvousOptions>()
            .Bind(configuration.GetSection(RendezvousOptions.SectionName))
            .Validate(options => { options.Validate(); return true; }, "Rendezvous settings are not usable.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IDeviceRegistry>(sp =>
        {
            var connectionString = PostgresConnectionString(configuration);
            if (connectionString is not null)
            {
                var logger = sp.GetRequiredService<ILogger<PostgresDeviceRegistry>>();
                return new PostgresDeviceRegistry(connectionString, logger);
            }

            return new InMemoryDeviceRegistry();
        });

        // If Postgres is in use, initialize the schema on startup.
        services.AddHostedService<PostgresSchemaInitializer>();

        // The counters exist whether or not the endpoint is served: the paths that count never ask whether anyone is
        // reading, and an increment nobody scrapes is cheaper than a branch around it.
        services.AddSingleton<ServerMetrics>();
        services.AddSingleton<DevicePresence>();
        services.AddSingleton<PresenceRateLimiter>();
        services.AddSingleton<ConnectionQuota>();

        // What one address may cost the server, and what one device may ask of it. Both are keyed and both shrink
        // again on their own; see KeyedTokenBucket.
        services.AddSingleton(sp => new KeyedTokenBucket<IPAddress>(
            sp.GetRequiredService<IOptions<RendezvousOptions>>().Value.RegistrationBurst,
            sp.GetRequiredService<IOptions<RendezvousOptions>>().Value.RegistrationRefill,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new KeyedTokenBucket<uint>(
            sp.GetRequiredService<IOptions<RendezvousOptions>>().Value.ConnectRequestBurst,
            sp.GetRequiredService<IOptions<RendezvousOptions>>().Value.ConnectRequestRefill,
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<PresenceService>();
        services.AddSingleton<ConnectionBroker>();
        services.AddSingleton<SignalingEndpoint>();
        services.AddSingleton<AddressReflector>();
        services.AddHostedService(sp => sp.GetRequiredService<AddressReflector>());
        // /health answers for the whole service, the device registry included: a rendezvous that cannot reach its
        // database cannot hand out the device id it already promised, so calling it healthy would be a lie that
        // monitoring believes. Reaching into the registry's own store is the only way to know.
        var health = services.AddHealthChecks();
        if (IsPostgres(configuration))
        {
            health.AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        }

        return services;
    }

    private sealed class PostgresSchemaInitializer(IDeviceRegistry registry) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (registry is PostgresDeviceRegistry postgres)
            {
                await postgres.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public static WebApplication MapRendezvous(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseForwardedHeaders(ForwardedHeaderOptions(app));

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.MapHealthChecks("/health");

        // Left unmapped when it is off, rather than mapped and refused: a public server neither advertises its device
        // count nor acknowledges that it has anything to hide there.
        if (app.Services.GetRequiredService<IOptions<RendezvousOptions>>().Value.MetricsEnabled)
        {
            app.MapGet(MetricsPath, (HttpContext context, ServerMetrics metrics, ConnectionQuota quota, DevicePresence presence) =>
                metrics.WriteAsync(context, quota.Open, presence.Count));
        }

        app.Map(SignalingPath, (HttpContext context, SignalingEndpoint endpoint) => endpoint.HandleAsync(context));
        return app;
    }

    /// <summary>The registry's connection string, or <c>null</c> when ids are kept in memory only.</summary>
    private static string? PostgresConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetSection(RendezvousOptions.SectionName).Get<RendezvousOptions>()?.PostgresConnectionString
            ?? configuration.GetConnectionString("Postgres");

        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private static bool IsPostgres(IConfiguration configuration) => PostgresConnectionString(configuration) is not null;

    /// <summary>
    /// The addresses the per-address limits are counted against. Behind a reverse proxy the socket address is the
    /// proxy's, so the client's own address only exists in <c>X-Forwarded-For</c> — a header anybody can write, which
    /// is why only the proxies named in the settings are believed. The framework's loopback default is kept.
    /// </summary>
    private static ForwardedHeadersOptions ForwardedHeaderOptions(WebApplication app)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };

        foreach (var entry in app.Services.GetRequiredService<IOptions<RendezvousOptions>>().Value.TrustedProxies)
        {
            // Validated at start-up, so both parses are known to succeed here.
            if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else if (System.Net.IPNetwork.TryParse(entry, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
        }

        return options;
    }
}
