namespace RemoteDesktop.Application.Hosting;

/// <summary>
/// Where the product keeps its state. Decided here rather than at each call site because the folder that looks right
/// on Windows is the wrong one elsewhere, and <see cref="Environment.SpecialFolder"/> quietly answers something
/// plausible instead of failing: <c>ApplicationData</c> is <c>~/.config</c> on Linux, which holds configuration and
/// not data, and <c>CommonApplicationData</c> is <c>/usr/share</c>, which belongs to the distribution.
/// </summary>
public static class DataDirectory
{
    private const string ProductName = "RemoteDesktop";

    /// <summary>
    /// The signed-in user's copy: the client's identity, remembered devices and logs.
    /// </summary>
    public static string ForUser() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName)
        : PosixDataHome();

    /// <summary>
    /// The machine's copy: what the always-on side keeps, so that it survives signing out.
    /// </summary>
    /// <remarks>
    /// The same folder as <see cref="ForUser"/> off Windows, and that is the point rather than a shortcut: the
    /// controlled end runs as the signed-in user inside that user's session, because that is the only place the
    /// desktop portals will talk to, so there is no second account whose state would need keeping apart from this one.
    /// </remarks>
    public static string ForMachine() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName)
        : PosixDataHome();

    private static string PosixDataHome()
    {
        // XDG_DATA_HOME is what the desktop expects application data to be under, and ~/.local/share is the value it
        // defaults to when unset.
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrEmpty(dataHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", ProductName)
            : Path.Combine(dataHome, ProductName);
    }
}
