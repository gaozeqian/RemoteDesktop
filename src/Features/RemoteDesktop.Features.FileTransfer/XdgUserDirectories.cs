namespace RemoteDesktop.Features.FileTransfer;

/// <summary>
/// The folders the desktop says a person's documents, downloads and videos are in.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Environment.SpecialFolder"/> answers with a fixed English name under the home directory, and on a
/// desktop that is not in English those folders do not have that name. On a Chinese install the desktop is
/// <c>~/桌面</c> and the documents are <c>~/文档</c>; asking for <c>SpecialFolder.DesktopDirectory</c> there returns a
/// path that does not exist, so the file browser offers nothing and a recording is written somewhere the person will
/// never look for it.
/// </para>
/// <para>
/// The desktop's own answer is in <c>~/.config/user-dirs.dirs</c>, which is where every toolkit reads it from. What
/// is read here is that file, not a guess at what the names might be.
/// </para>
/// <para>
/// Windows has no equivalent and keeps using <see cref="Environment.SpecialFolder"/>, which is correct there.
/// </para>
/// </remarks>
public static class XdgUserDirectories
{
    private const string FileName = "user-dirs.dirs";

    /// <summary>Where the desktop is, or <c>null</c> when it is not set or not there.</summary>
    public static string? Desktop => Resolve("XDG_DESKTOP_DIR");

    public static string? Documents => Resolve("XDG_DOCUMENTS_DIR");

    public static string? Downloads => Resolve("XDG_DOWNLOAD_DIR");

    public static string? Videos => Resolve("XDG_VIDEOS_DIR");

    /// <summary>
    /// The folder the desktop records into, falling back to the one Windows would have used.
    /// </summary>
    /// <remarks>
    /// The fallback matters for the case where the person has never set one: recording into a folder that does not
    /// exist fails, and one that is merely in an unexpected place does not.
    /// </remarks>
    public static string RecordingDirectory()
    {
        if (Videos is { } videos)
        {
            return videos;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    /// <summary>Reads one setting out of the desktop's file, expanding the <c>$HOME</c> it is written with.</summary>
    private static string? Resolve(string key)
    {
        // Windows has no such file, and the callers use SpecialFolder there.
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        var configuration = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var directory = string.IsNullOrEmpty(configuration) ? Path.Combine(home, ".config") : configuration;
        var path = Path.Combine(directory, FileName);

        string? value;
        try
        {
            value = File.Exists(path) ? FindValue(File.ReadAllLines(path), key) : null;
        }
        catch (IOException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var expanded = Expand(value, home);

        // A folder set to the home directory is how the file says "this one is switched off" — it is what the settings
        // tool writes when a person turns the desktop off, and offering the whole home directory as "Desktop" would be
        // wrong in a way that looks like it worked.
        if (string.IsNullOrEmpty(expanded) || string.Equals(expanded, home, StringComparison.Ordinal))
        {
            return null;
        }

        // Only if it is really there: a folder named in the file but never created is not somewhere to send anyone.
        return Directory.Exists(expanded) ? expanded : null;
    }

    /// <summary>The value of <c>KEY=...</c> in the file, with the quotes the file writes it with taken off.</summary>
    private static string? FindValue(string[] lines, string key)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key, StringComparison.Ordinal))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var value = trimmed[(separator + 1)..].Trim();

            // The file quotes its values, but a hand-edited one may not.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            return value;
        }

        return null;
    }

    /// <summary>
    /// Turns <c>$HOME/…</c> into a path.
    /// </summary>
    /// <remarks>
    /// The file is written in the shell's spelling, so the home directory appears as <c>$HOME</c> — and a value is
    /// never expanded from the environment, because the file is read on behalf of a person whose environment this
    /// process does not have.
    /// </remarks>
    private static string Expand(string value, string home)
    {
        if (value.StartsWith("$HOME/", StringComparison.Ordinal))
        {
            return Path.Combine(home, value["$HOME/".Length..]);
        }

        if (value == "$HOME")
        {
            return home;
        }

        if (value.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(home, value[2..]);
        }

        return value;
    }
}
