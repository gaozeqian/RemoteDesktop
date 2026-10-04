using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Application.Features;

namespace RemoteDesktop.Features.Camera;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Lets controllers look through this computer's camera, once the person here has allowed it. Requires camera ports,
    /// an <c>IUserContextGuard</c> from the platform and the media services (<c>AddRemoteDesktopMedia</c>).
    /// </summary>
    public static IServiceCollection AddCameraFeature(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IFeatureModule, CameraModule>();
        return services;
    }
}
