using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Application.Features;

namespace RemoteDesktop.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRemoteDesktopApplication(this IServiceCollection services)
    {
        services.AddSingleton<ChannelDispatcher>();
        return services;
    }
}
