using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Application.Features;

namespace RemoteDesktop.Features.Terminal;

public static class ServiceCollectionExtensions
{
    /// <summary>Lets controllers open shells on this computer. Requires an <c>IPtyHost</c> and an <c>IUserContextGuard</c> from the platform.</summary>
    public static IServiceCollection AddTerminalFeature(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IFeatureModule, TerminalModule>();
        return services;
    }
}
