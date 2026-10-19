using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Shared;
using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>Composition of the rendezvous server, shared by <c>Program</c> and the integration tests.</summary>
public static class RendezvousServer
{
    public const string SignalingPath = "/ws";

    /// <summary>Mapped only when <see cref="RendezvousOptions.MetricsEnabled"/> is set; off, it is a 404.</summary>
    public const string MetricsPath = "/metrics";

    public static IServiceCollection AddRendezvous(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RendezvousOptions>()
            .Bind(configuration.GetSection(RendezvousOptions.SectionName))
            .Validate(options => { options.Validate(); return true; }, "Rendezvous settings are not usable.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);

        // Where device ids are kept. Resolved and checked here rather than in the options validator because the
        // legacy fallback (ConnectionStrings:Postgres) lives in configuration rather than in the bound section —
        // and resolving it here still stops a misconfigured deployment at start-up, which is what the validator is
        // for. The bound section is read twice; that is the same thing the previous version did for one key.
        var options = configuration.GetSection(RendezvousOptions.SectionName).Get<RendezvousOptions>() ?? new RendezvousOptions();
        var database = DatabaseSettings.Resolve(options.Database, LegacyConnectionString(options, configuration));
        database.Validate();
        services.AddDeviceRegistry(database);

        // No endpoint configured is the normal case for a deployment without a console: the no-op sink keeps every
        // call site identical.
        services.AddOpsEvents(configuration, options.Name);

        // A store is a database, and a database has a schema to create and a connection to watch. A registry that
        // is not a store — the in-memory one — has neither, so this is the whole condition.
        var persistent = database.Provider != DatabaseSettings.None;
        if (persistent)
        {
            services.AddHostedService<DeviceRegistryInitializer>();
        }

        // What this process is, for the console's node list. Registered here rather than computed per request so the
        // start time is the start time.
        services.AddSingleton(new NodeInfo(
            options.Name,
            "rendezvous",
            ProductVersion.Display,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

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
        services.AddSingleton<NodeEventPublisher>();
        services.AddSingleton<ConnectionBroker>();
        services.AddSingleton<TelemetryReceiver>();
        services.AddSingleton<SignalingEndpoint>();
        services.AddSingleton<AddressReflector>();
        services.AddHostedService(sp => sp.GetRequiredService<AddressReflector>());

        // /health answers for the whole service, the device registry included: a rendezvous that cannot reach its
        // database cannot hand out the device id it already promised, so calling it healthy would be a lie that
        // monitoring believes. Asking the registered store is the only way to know — and the only way that cannot
        // disagree with the registry about which database it is.
        var health = services.AddHealthChecks();
        if (persistent)
        {
            health.AddCheck<DeviceRegistryHealthCheck>("database", tags: ["ready"]);
        }

        return services;
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
            app.MapGet(MetricsPath, async (HttpContext context, ServerMetrics metrics, ConnectionQuota quota, DevicePresence presence, IOptions<RendezvousOptions> options) =>
            {
                if (!NodeTokenGuard.IsAuthorized(context.Request.Headers.Authorization.ToString(), options.Value.NodeToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await metrics.WriteAsync(context, quota.Open, presence.Count).ConfigureAwait(false);
            });
        }

        // What this node is, for the console's node list. The same shape the relay serves, from the same contract.
        app.MapGet("/internal/v1/info", (HttpContext context, NodeInfo info, IOptions<RendezvousOptions> options) =>
            NodeTokenGuard.IsAuthorized(context.Request.Headers.Authorization.ToString(), options.Value.NodeToken)
                ? Results.Ok(info)
                : Results.Unauthorized());

        // A page of registered devices. Read-only, paged by device id, and the only way the console learns about
        // devices: it never opens this server's database.
        app.MapGet("/internal/v1/devices", async (
            HttpContext context,
            IDeviceCatalog catalog,
            DevicePresence presence,
            IOptions<RendezvousOptions> options,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            if (!NodeTokenGuard.IsAuthorized(context.Request.Headers.Authorization.ToString(), options.Value.NodeToken))
            {
                return Results.Unauthorized();
            }

            var after = uint.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            var pageSize = Math.Clamp(limit ?? 50, 1, 200);

            // One row more than asked for is how "is there a next page" is answered without counting the table.
            var records = await catalog.ListAsync(after, pageSize + 1, cancellationToken).ConfigureAwait(false);
            var hasMore = records.Count > pageSize;
            var page = hasMore ? records.Take(pageSize).ToArray() : [.. records];
            var online = presence.FilterOnline([.. page.Select(record => record.DeviceId)]).ToHashSet();

            NodeDeviceSummary[] devices =
            [
                .. page.Select(record => new NodeDeviceSummary(
                    record.DeviceId,
                    record.FirstSeenUnixMilliseconds,
                    record.LastSeenUnixMilliseconds,
                    online.Contains(record.DeviceId),
                    record.Revoked)),
            ];

            return Results.Ok(new NodeDevicePage(
                devices,
                hasMore ? page[^1].DeviceId.ToString(CultureInfo.InvariantCulture) : null));
        });

        app.MapNodeAdmin();

        app.Map(SignalingPath, (HttpContext context, SignalingEndpoint endpoint) => endpoint.HandleAsync(context));
        return app;
    }

    /// <summary>
    /// The connection string earlier versions were configured with, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// Two keys, in the order they were introduced. They still mean "use PostgreSQL": a deployment that has one and
    /// upgrades must keep every device code it has handed out, so this fallback outlives the settings that name it.
    /// </remarks>
    private static string? LegacyConnectionString(RendezvousOptions options, IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(options.PostgresConnectionString)
            ? configuration.GetConnectionString("Postgres")
            : options.PostgresConnectionString;

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
