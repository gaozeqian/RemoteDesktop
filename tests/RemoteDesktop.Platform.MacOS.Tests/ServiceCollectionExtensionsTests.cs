using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.MacOS.Clipboard;
using RemoteDesktop.Platform.MacOS.Display;
using RemoteDesktop.Platform.MacOS.Input;
using RemoteDesktop.Platform.MacOS.Terminal;

namespace RemoteDesktop.Platform.MacOS.Tests;

/// <summary>
/// Checks that <see cref="ServiceCollectionExtensions.AddMacOSPlatform"/> wires up every port the platform project
/// implements, with the implementation that was written for it.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void Every_port_that_has_an_implementation_is_registered()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS platform is macOS only.");

        var services = new ServiceCollection();
        services.AddMacOSPlatform(Path.Combine(Path.GetTempPath(), "remote-desktop-platform-tests"));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsType<KeychainSecretStore>(provider.GetRequiredService<ISecretStore>());
        Assert.IsType<CoreGraphicsDisplays>(provider.GetRequiredService<IDisplayEnumerator>());
        Assert.IsType<CGEventInputInjector>(provider.GetRequiredService<IInputInjector>());
        Assert.IsType<MacUserContextGuard>(provider.GetRequiredService<IUserContextGuard>());
        Assert.IsType<PosixPtyHost>(provider.GetRequiredService<IPtyHost>());
        Assert.IsType<IOPMAssertionKeepAwake>(provider.GetRequiredService<ISystemKeepAwake>());
        Assert.IsType<MacClipboard>(provider.GetRequiredService<IClipboard>());
    }
}
