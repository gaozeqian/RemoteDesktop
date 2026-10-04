using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Application.Features;

namespace RemoteDesktop.Features.Desktop;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopFeature(this IServiceCollection services)
    {
        services.AddSingleton<IFeatureModule, VideoChannelModule>();
        services.AddSingleton<IFeatureModule, InputChannelModule>();
        services.AddSingleton<IFeatureModule, ClipboardChannelModule>();
        services.AddSingleton<IFeatureModule, AudioChannelModule>();
        return services;
    }
}
