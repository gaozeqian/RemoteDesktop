using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;

namespace RemoteDesktop.Hosts.Composition;

/// <summary>
/// The features a controlled end serves, in one place.
/// </summary>
/// <remarks>
/// The client (when it hosts incoming sessions itself), the Windows service's Unix counterparts and the Windows
/// session agent all register the same four modules. They used to repeat the list in four files, so a fifth feature
/// or a changed ledger path was one edit away from being registered in three places and missed in the fourth.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <param name="dataDirectory">Where the file feature keeps its list of unfinished transfers.</param>
    public static IServiceCollection AddRemoteDesktopHostFeatures(this IServiceCollection services, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        return services
            .AddDesktopFeature()
            .AddFileTransferFeature(partialFileLedgerPath: Path.Combine(dataDirectory, "partial-files.log"))
            .AddTerminalFeature()
            .AddCameraFeature();
    }
}
