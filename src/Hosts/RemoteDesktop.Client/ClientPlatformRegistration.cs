using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Platform.Linux;
using RemoteDesktop.Platform.MacOS;
using RemoteDesktop.Platform.Windows;

namespace RemoteDesktop.Client;

/// <summary>Registers the platform ports of the operating system this process is running on.</summary>
/// <remarks>
/// Every platform project targets plain <c>net10.0</c> — deliberately, so that one host can reference all of them and
/// pick at run time, which is what lets a single client project build and run on Windows, macOS and Linux. The
/// registration methods carry <see cref="System.Runtime.Versioning.SupportedOSPlatformAttribute"/>, so the
/// platform-compatibility analyzer keeps this method honest: each call is only reachable from a matching OS check.
/// The ILLink trimmer evaluates those checks as constants for the target being published and removes the branch, and
/// with it the platform assembly that cannot run there.
/// <para>
/// Deliberately not part of <see cref="ClientHost"/>: that class declares which platforms it supports, and a class
/// declaration applies to every member, which would put a platform guard on this method that it does not have.
/// </para>
/// </remarks>
internal static class ClientPlatformRegistration
{
    /// <param name="dataDirectory">
    /// Where protected state lives. It also scopes the platform's secret store, so two data directories are two
    /// devices — the same rule every platform layer follows.
    /// </param>
    public static IServiceCollection AddClientPlatform(this IServiceCollection services, string dataDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            // DPAPI has a machine-or-user choice to make; the client runs as the signed-in user, so its secrets are
            // that user's. The keychain on macOS and the owner-only file on Linux are per-user by construction and
            // take no such argument.
            return services.AddWindowsPlatform(dataDirectory, DataProtectionScope.CurrentUser);
        }

        if (OperatingSystem.IsMacOS())
        {
            return services.AddMacOSPlatform(dataDirectory);
        }

        if (OperatingSystem.IsLinux())
        {
            return services.AddLinuxPlatform(dataDirectory);
        }

        // Unreachable: Program.Main rejects anything else before a host is built. Kept so that adding a platform
        // without wiring it up here fails loudly instead of starting a client with no screen, input or secrets.
        throw new PlatformNotSupportedException("Remote Desktop has no platform layer for this operating system.");
    }
}
