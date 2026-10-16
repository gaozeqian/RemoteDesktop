using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.MacOS;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

/// <summary>
/// Checks the SMAppService-backed login item port: that it resolves to the macOS implementation, and that reading its
/// state is side-effect free (only Register/Unregister mutate, and those require a signed .app in /Applications).
/// </summary>
public class LoginItemTests
{
    [Fact]
    public void Is_registered_as_the_login_item_port()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "SMAppService is macOS only.");

        var services = new ServiceCollection();
        services.AddMacOSPlatform(Path.Combine(Path.GetTempPath(), "remote-desktop-loginitem-tests"));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsType<MacLoginItem>(provider.GetRequiredService<ILoginItem>());
    }

    [Fact]
    public void Reports_a_stable_registration_state_without_side_effects()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "SMAppService is macOS only.");

        var services = new ServiceCollection();
        services.AddMacOSPlatform(Path.Combine(Path.GetTempPath(), "remote-desktop-loginitem-tests"));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var loginItem = provider.GetRequiredService<ILoginItem>();
        // Reading must not throw or change anything; Register/Unregister are the mutating calls.
        var first = loginItem.IsRegistered;
        var second = loginItem.IsRegistered;
        Assert.Equal(first, second);
    }
}
