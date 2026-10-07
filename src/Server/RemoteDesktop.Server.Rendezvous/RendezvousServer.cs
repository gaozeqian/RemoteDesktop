using Microsoft.Extensions.Options;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>Composition of the rendezvous server, shared by <c>Program</c> and the integration tests.</summary>
public static class RendezvousServer
{
    public const string SignalingPath = "/ws";

    public static IServiceCollection AddRendezvous(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RendezvousOptions>()
            .Bind(configuration.GetSection(RendezvousOptions.SectionName))
            .Validate(options => { options.Validate(); return true; }, "Rendezvous settings are not usable.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IDeviceRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RendezvousOptions>>().Value;
            var connectionString = options.PostgresConnectionString
                ?? configuration.GetConnectionString("Postgres");

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                var logger = sp.GetRequiredService<ILogger<PostgresDeviceRegistry>>();
                return new PostgresDeviceRegistry(connectionString, logger);
            }

            return new InMemoryDeviceRegistry();
        });

        // If Postgres is in use, initialize the schema on startup.
        services.AddHostedService<PostgresSchemaInitializer>();

        services.AddSingleton<DevicePresence>();
        services.AddSingleton<PresenceRateLimiter>();
        services.AddSingleton<PresenceService>();
        services.AddSingleton<ConnectionBroker>();
        services.AddSingleton<SignalingEndpoint>();
        services.AddSingleton<AddressReflector>();
        services.AddHostedService(sp => sp.GetRequiredService<AddressReflector>());
        services.AddHealthChecks();
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

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.MapHealthChecks("/health");
        app.Map(SignalingPath, (HttpContext context, SignalingEndpoint endpoint) => endpoint.HandleAsync(context));
        return app;
    }
}
