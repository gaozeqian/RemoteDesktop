using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Application.Features;

namespace RemoteDesktop.Features.Terminal;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTerminalFeature(this IServiceCollection services)
    {
        services.AddSingleton<IFeatureModule, TerminalModule>();
        return services;
    }
}
