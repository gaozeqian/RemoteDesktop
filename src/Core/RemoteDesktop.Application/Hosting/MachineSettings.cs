using Microsoft.Extensions.Configuration;

namespace RemoteDesktop.Application.Hosting;

/// <summary>
/// The settings the installer owns for the whole machine, outside the program's own directory:
/// <c>/Library/Application Support/RemoteDesktop/</c> on macOS and <c>%ProgramData%\RemoteDesktop</c> on Windows.
/// </summary>
/// <remarks>
/// <para>
/// The macOS bundles are signed, and editing a file inside a signed <c>.app</c> invalidates the signature — which is
/// the thing TCC keys the screen-recording and accessibility grants to. The installer therefore cannot write the
/// address into <c>Contents/MacOS/appsettings.json</c>; it writes here instead, outside the bundle, and both the
/// client and the host read it as the layer between the shipped defaults and the person's own file.
/// </para>
/// <para>
/// Windows has the same problem for a different reason: the MSI's major upgrade removes the old product's files
/// before installing the new ones, so a live <c>appsettings.json</c> under Program Files is deleted on every upgrade.
/// The installer writes here instead, and the MSI does not own this directory. The shipped file next to the program
/// stays the set of defaults; this one carries the address the deployment chose.
/// </para>
/// <para>
/// On Linux the installed <c>appsettings.json</c> in <c>/etc/remotedesktop-server</c> is already outside the program
/// directory and is not replaced by an upgrade, so this layer is not needed there.
/// </para>
/// </remarks>
public static class MachineSettings
{
    /// <summary>Where the macOS installer writes the machine-wide settings.</summary>
    public const string MacOSDirectory = "/Library/Application Support/RemoteDesktop";

    /// <summary>The file itself.</summary>
    public static string MacOSPath => Path.Combine(MacOSDirectory, "appsettings.json");

    /// <summary>Where the Windows installer writes the machine-wide settings: <c>%ProgramData%\RemoteDesktop</c>.</summary>
    public static string WindowsPath => Path.Combine(DataDirectory.ForMachine(), "appsettings.json");

    /// <summary>The path on this machine, or <c>null</c> where the platform has no such layer.</summary>
    public static string? PathForThisMachine() => OperatingSystem.IsMacOS()
        ? MacOSPath
        : OperatingSystem.IsWindows() ? WindowsPath : null;

    /// <summary>
    /// Layers the machine settings under the person's own file. Call this before
    /// <see cref="UserSettings.AddConfigurationSource"/>; both insert after the shipped file, so the later call ends
    /// up on top.
    /// </summary>
    public static void AddConfigurationSource(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var path = PathForThisMachine();
        if (path is not null)
        {
            SettingsFile.AddConfigurationSource(configuration, path);
        }
    }
}
