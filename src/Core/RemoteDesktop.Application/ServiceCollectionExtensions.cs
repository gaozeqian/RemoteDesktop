using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;

namespace RemoteDesktop.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Session use cases. The host composition must also register a transport (e.g. <c>AddQuicTransport</c>).
    /// </summary>
    public static IServiceCollection AddRemoteDesktopApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ChannelDispatcher>();
        services.AddSingleton<HostCredentials>();
        services.AddSingleton<AuthAttemptLimiter>();
        services.AddSingleton<ControllerSessionService>();
        services.AddSingleton<HostSessionService>();
        services.AddHostedService<TransportWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<HostSessionService>());
        return services;
    }
}
