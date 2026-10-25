using Microsoft.Extensions.Configuration;

namespace RemoteDesktop.Application.Hosting;

/// <summary>
/// The settings the installer owns for the whole machine, on macOS: <c>/Library/Application Support/RemoteDesktop/</c>.
/// </summary>
/// <remarks>
/// <para>
/// The macOS bundles are signed, and editing a file inside a signed <c>.app</c> invalidates the signature — which is
/// the thing TCC keys the screen-recording and accessibility grants to. The installer therefore cannot write the
/// address into <c>Contents/MacOS/appsettings.json</c>; it writes here instead, outside the bundle, and both the
/// client and the host read it as the layer between the shipped defaults and the person's own file.
/// </para>
/// <para>
/// On Windows there is already a machine-level file: the service's own <c>appsettings.json</c> next to the program.
/// On Linux the installed <c>appsettings.json</c> next to the executable is writable by the package and is edited by
/// its postinst. This layer exists only where the program file belongs to a signed bundle.
/// </para>
/// </remarks>
public static class MachineSettings
{
    /// <summary>Where the macOS installer writes the machine-wide defaults.</summary>
    public const string MacOSDirectory = "/Library/Application Support/RemoteDesktop";

    /// <summary>The file itself.</summary>
    public static string MacOSPath => Path.Combine(MacOSDirectory, "appsettings.json");

    /// <summary>The path on this machine, or <c>null</c> where the platform has no such layer.</summary>
    public static string? PathForThisMachine() => OperatingSystem.IsMacOS() ? MacOSPath : null;

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
