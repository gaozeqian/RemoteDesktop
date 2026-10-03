using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Application.Features;

namespace RemoteDesktop.Features.Camera;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCameraFeature(this IServiceCollection services)
    {
        services.AddSingleton<IFeatureModule, CameraModule>();
        return services;
    }
}
