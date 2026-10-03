using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Application.Features;

namespace RemoteDesktop.Features.FileTransfer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileTransferFeature(this IServiceCollection services)
    {
        services.AddSingleton<IFeatureModule, FileTransferModule>();
        return services;
    }
}
