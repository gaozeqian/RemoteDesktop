namespace RemoteDesktop.Server.Rendezvous;

/// <summary>Composition of the rendezvous server, shared by <c>Program</c> and the integration tests.</summary>
public static class RendezvousServer
{
    public const string SignalingPath = "/ws";

    public static IServiceCollection AddRendezvous(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RendezvousOptions>(configuration.GetSection(RendezvousOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDeviceRegistry, InMemoryDeviceRegistry>();
        services.AddSingleton<DevicePresence>();
        services.AddSingleton<ConnectionBroker>();
        services.AddSingleton<SignalingEndpoint>();
        services.AddSingleton<AddressReflector>();
        services.AddHostedService(sp => sp.GetRequiredService<AddressReflector>());
        services.AddHealthChecks();
        return services;
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
