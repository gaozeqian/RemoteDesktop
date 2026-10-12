using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.MacOS.Display;
using RemoteDesktop.Platform.MacOS.Input;
using RemoteDesktop.Platform.MacOS.Terminal;

namespace RemoteDesktop.Platform.MacOS;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the macOS implementations of the platform ports: Keychain secrets, CoreGraphics display enumeration
    /// and event injection, ScreenCaptureKit capture, the BSD pseudo-terminal, and the IOKit power assertion.
    /// </summary>
    /// <remarks>
    /// There is no scope argument the way Windows has one — DPAPI has a machine-or-user choice to make, the keychain
    /// is per-user by construction. The ports that are missing are the ones with no macOS implementation yet
    /// (codecs, clipboard, camera, audio, recording); each arrives with the milestone that provides it, and until
    /// then the container refuses to build a feature that needs it, which is the behaviour we want.
    /// </remarks>
    /// <param name="dataDirectory">
    /// Where protected state lives. It also scopes the keychain items, so two data directories are two devices — the
    /// same rule the Windows registration follows.
    /// </param>
    [SupportedOSPlatform("macos")]
    public static IServiceCollection AddMacOSPlatform(this IServiceCollection services, string dataDirectory)
    {
        services.AddSingleton<ISecretStore>(new KeychainSecretStore(dataDirectory));
        services.AddSingleton<IDisplayEnumerator, CoreGraphicsDisplays>();
        services.AddSingleton<IScreenCapturerFactory, ScreenCaptureKitCapturerFactory>();
        services.AddSingleton<IInputInjector, CGEventInputInjector>();
        services.AddSingleton<IUserContextGuard, MacUserContextGuard>();
        services.AddSingleton<IPtyHost, PosixPtyHost>();
        services.AddSingleton<ISystemKeepAwake, IOPMAssertionKeepAwake>();
        return services;
    }
}
