using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteDesktop.Application.Features;

namespace RemoteDesktop.Features.FileTransfer;

public static class ServiceCollectionExtensions
{
    /// <summary>Serves files to controllers. Requires an <c>IFileAccessGuard</c> from the platform.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="policy">What the controller may reach; defaults to everything the signed-in user can.</param>
    public static IServiceCollection AddFileTransferFeature(this IServiceCollection services, FileAccessPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(policy ?? FileAccessPolicy.Unrestricted);
        services.AddSingleton<IFeatureModule, FileTransferModule>();
        return services;
    }
}
