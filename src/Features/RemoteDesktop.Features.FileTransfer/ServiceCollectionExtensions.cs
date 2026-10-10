using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteDesktop.Application.Features;

namespace RemoteDesktop.Features.FileTransfer;

public static class ServiceCollectionExtensions
{
    /// <summary>Serves files to controllers. Requires an <c>IUserContextGuard</c> from the platform.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="policy">What the controller may reach; defaults to everything the signed-in user can.</param>
    /// <param name="partialFileLedgerPath">
    /// Where to keep the list of unfinished transfers, so the ones nobody comes back for can be deleted. Empty (the
    /// default) keeps no list: nothing is swept, which is what a process without a data directory of its own wants.
    /// </param>
    public static IServiceCollection AddFileTransferFeature(
        this IServiceCollection services,
        FileAccessPolicy? policy = null,
        string? partialFileLedgerPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(policy ?? FileAccessPolicy.Unrestricted);

        var ledger = new PartialFileLedgerOptions(partialFileLedgerPath ?? string.Empty);
        services.AddSingleton(ledger);
        if (ledger.IsEnabled)
        {
            services.AddSingleton<PartialFileLedger>();
            services.AddHostedService<PartialFileSweeper>();
        }

        services.AddSingleton<IFeatureModule, FileTransferModule>();
        return services;
    }
}
