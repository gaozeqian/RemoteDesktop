using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Windows;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Windows implementations of the platform ports. Capture (DXGI), input (SendInput), hardware
    /// encoding (Media Foundation), ConPTY, WASAPI and camera adapters are added milestone by milestone.
    /// </summary>
    /// <param name="dataDirectory">Where protected state is stored (%ProgramData% for the service, %AppData% for the UI).</param>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services, string dataDirectory, DataProtectionScope scope)
    {
        services.AddSingleton<ISecretStore>(new DpapiSecretStore(Path.Combine(dataDirectory, "secrets"), scope));
        return services;
    }
}
