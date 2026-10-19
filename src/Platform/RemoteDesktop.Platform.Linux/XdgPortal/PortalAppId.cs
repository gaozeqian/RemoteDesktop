using System.Globalization;
using System.Text.RegularExpressions;

namespace RemoteDesktop.Platform.Linux.XdgPortal;

/// <summary>
/// The application id the desktop portal derives for this process, when there is one.
/// </summary>
/// <remarks>
/// <para>
/// The camera permission store is keyed by this id, and the portal derives it from the systemd user unit the process
/// runs in: <c>app-&lt;ApplicationID&gt;-&lt;random&gt;.scope</c> for an application launched by the desktop, and
/// <c>app-&lt;ApplicationID&gt;[.service]</c> for one the desktop starts as a service. A process with no such unit has
/// an empty id, and the cache stores nothing for it. This class mirrors that derivation so the host can write the
/// entry that the portal will later read.
/// </para>
/// <para>
/// A unit name alone is not enough: the portal then asks <c>GDesktopAppInfo</c> for <c>&lt;ApplicationID&gt;.desktop</c>
/// and treats a missing file as "no app id". Both halves are checked here, because a permission written under an id
/// the portal will not use is a grant that silently does nothing.
/// </para>
/// </remarks>
internal static partial class PortalAppId
{
    /// <summary>The id for the current process, or <see langword="null"/> when the portal would use an empty one.</summary>
    public static string? FromCurrentProcess() => FromCgroup(ReadCgroup());

    /// <summary>The id a cgroup's unit name maps to, or <see langword="null"/> when the unit is not an application unit.</summary>
    internal static string? FromCgroup(string? cgroup)
    {
        if (string.IsNullOrEmpty(cgroup))
        {
            return null;
        }

        foreach (var line in cgroup.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var slash = line.LastIndexOf('/');
            var unit = (slash >= 0 ? line[(slash + 1)..] : line).Trim();
            if (ParseUnitName(unit) is { } id && DesktopFileExists(id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the portal's two unit-name patterns. Kept separate from the cgroup and file checks so the naming rules
    /// can be tested without a systemd session.
    /// </summary>
    internal static string? ParseUnitName(string unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (!unit.StartsWith("app-", StringComparison.Ordinal))
        {
            return null;
        }

        var match = ScopeRegex().Match(unit);
        if (!match.Success)
        {
            match = ServiceRegex().Match(unit);
        }

        if (!match.Success)
        {
            return null;
        }

        return Unescape(match.Groups[1].Value);
    }

    private static string ReadCgroup()
    {
        try
        {
            return File.ReadAllText("/proc/self/cgroup");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool DesktopFileExists(string appId)
    {
        var directories = new List<string>();
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(dataHome))
        {
            directories.Add(dataHome);
        }

        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"));

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        directories.AddRange(
            string.IsNullOrEmpty(dataDirs)
                ? ["/usr/local/share", "/usr/share"]
                : dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries));

        foreach (var directory in directories.Distinct(StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(directory, "applications", appId + ".desktop")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Unit names escape characters such as <c>-</c> as <c>\x2d</c>; the id is the unescaped form.</summary>
    private static string Unescape(string value) =>
        Regex.Replace(
            value,
            @"\\x([0-9A-Fa-f]{2})",
            match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToString());

    [GeneratedRegex(@"^app-(?:[A-Za-z0-9]+-)?(.+?)(?:-[A-Za-z0-9]*)\.scope$", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeRegex();

    [GeneratedRegex(@"^app-(?:[A-Za-z0-9]+-)?(.+?)(?:@[A-Za-z0-9]*|-autostart)?\.service$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceRegex();
}
