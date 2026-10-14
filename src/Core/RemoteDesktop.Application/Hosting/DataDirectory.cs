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
    public static string ForUser() => Path.Combine(UserDataRoot(), ProductName);

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
        : ForUser();

    /// <summary>
    /// The per-user root, which each system spells differently — and on one of them the obvious member is the wrong
    /// kind of folder.
    /// </summary>
    /// <remarks>
    /// On Windows <c>ApplicationData</c> is <c>%APPDATA%</c> — roaming, which is where the identity should travel
    /// with the account — and on macOS it is <c>~/Library/Application Support</c>, the folder a Mac's own tools and
    /// backups expect application state to be in (verified on the machine the port was written on). On Linux the
    /// same member is <c>~/.config</c> — configuration, not data — so Linux reads the desktop's own convention
    /// instead: <c>$XDG_DATA_HOME</c>, defaulting to <c>~/.local/share</c>.
    /// </remarks>
    private static string UserDataRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrEmpty(dataHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : dataHome;
    }
}
