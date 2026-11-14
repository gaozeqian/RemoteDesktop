using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// Launches this application at login through <c>SMAppService.mainAppService</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the macOS side of "unattended controlled-side": a host that should be reachable without someone signing in
/// first registers itself here. The grant is keyed to the code-signing identity, so it only sticks for a properly
/// signed <c>.app</c> in <c>/Applications</c> — an ad-hoc signature is a different identity every build, and
/// <see cref="IsRegistered"/> reports <c>false</c> for it. A development-signed build keeps its grant across rebuilds
/// only as long as the signing identity does not change.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacLoginItem : ILoginItem
{
    public bool IsRegistered => MacNativeInterop.rd_login_item_is_registered() != 0;

    public void Register()
    {
        if (MacNativeInterop.rd_login_item_register() != MacNativeInterop.Ok)
        {
            throw new InvalidOperationException(
                "This application could not be registered as a login item. It must be a signed .app in /Applications.");
        }
    }

    public void Unregister()
    {
        if (MacNativeInterop.rd_login_item_unregister() != MacNativeInterop.Ok)
        {
            throw new InvalidOperationException("This application could not be removed as a login item.");
        }
    }
}
