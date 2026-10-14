using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>
/// What paths a controller may name. Every path from the network goes through here first: it must be absolute, free of
/// tricks (device paths, alternate data streams, reserved names) and, when roots are configured, inside one of them
/// once <c>..</c> and friends are resolved. Access beyond that is up to the operating system, since the files are
/// reached with the rights of the logged-in user.
/// </summary>
public sealed class FileAccessPolicy
{
    private const int MaxPathLength = 32_000;
    private const int MaxNameLength = 255;

    /// <summary>How many links deep the resolution walk follows before it judges the spelling it has. The operating
    /// systems refuse a longer chain themselves (ELOOP after 32 on Linux, fewer on Windows), so no real path reaches
    /// this; it exists so a planted cycle of links ends the walk instead of hanging it.</summary>
    private const int MaxLinkDepth = 40;

    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    /// <summary>Windows compares paths without regard to case; a Unix filesystem does not.</summary>
    private static readonly StringComparison PathComparison =
        IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Characters a name may not contain. Only the separator and NUL are impossible everywhere; the rest are the
    /// Windows-only set, and refusing them on Unix would reject names the filesystem accepts perfectly well (a colon
    /// and a backslash are ordinary characters there).
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> InvalidNameCharacters = IsWindows
        ? System.Buffers.SearchValues.Create(['\\', '/', ':', '*', '?', '"', '<', '>', '|'])
        : System.Buffers.SearchValues.Create(['/', '\0']);

    /// <summary>
    /// Kernel interfaces that are never a file worth transferring. <c>/proc/kcore</c> and <c>/proc/self/mem</c> read
    /// a process's memory and <c>/dev/sda</c> is the raw disk, so on a host with no roots configured they would
    /// otherwise be reachable through the ordinary file browser.
    /// </summary>
    /// <remarks>
    /// <c>/run</c> is deliberately not here even though it holds runtime state: removable media is mounted under
    /// <c>/run/media/&lt;user&gt;/…</c>, and copying a file off a memory stick is exactly what the file feature is for.
    /// </remarks>
    private static readonly string[] UnixKernelPaths = ["/proc", "/sys", "/dev"];

    private readonly string[] _allowedRoots;

    /// <param name="allowedRoots">Folders the controller may work in; empty means everything the user can reach.</param>
    public FileAccessPolicy(IEnumerable<string>? allowedRoots = null) =>
        // The roots are resolved the same way a candidate path is: a root that is itself reached through a symbolic
        // link (a common way to give a folder a stable name) would otherwise never match anything, because the
        // candidate is resolved and the root would not be.
        _allowedRoots = [.. (allowedRoots ?? []).Select(root => ResolveFinalTarget(TrimTrailingSeparator(Path.GetFullPath(root))))];

    public static FileAccessPolicy Unrestricted { get; } = new();

    /// <summary>Validates and canonicalizes a path received from the other side.</summary>
    /// <exception cref="FileOperationException"><see cref="FileError.InvalidPath"/> or <see cref="FileError.NotAllowed"/>.</exception>
    public string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path.Contains('\0', StringComparison.Ordinal))
        {
            throw Invalid("The path is empty or malformed.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw Invalid("The path must be absolute.");
        }

        if (IsWindows)
        {
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                throw Invalid("Device paths are not allowed.");
            }

            // Only a drive letter may contain a colon; anything else names an alternate data stream. This is a Windows
            // idea through and through — on Unix a colon is an ordinary character, so the check has to stay inside the
            // Windows branch or every path containing one would be refused.
            var afterDrive = path.Length >= 2 && path[1] == ':' ? path[2..] : path;
            if (afterDrive.Contains(':', StringComparison.Ordinal))
            {
                throw Invalid("Alternate data streams are not allowed.");
            }
        }

        string full;
        try
        {
            full = TrimTrailingSeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FileOperationException(FileError.InvalidPath, "The path is not valid.", ex);
        }

        if (IsWindows && full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IsReserved))
        {
            throw Invalid("The path contains a reserved device name.");
        }

        var resolved = ResolveFinalTarget(full);

        // Judged on the resolved path, not the spelling: a symbolic link pointing into /proc would otherwise be a way
        // straight past this check.
        if (!IsWindows && UnixKernelPaths.Any(kernel => IsSameOrInside(resolved, kernel)))
        {
            throw Invalid("That location is a kernel interface, not a file.");
        }

        if (_allowedRoots.Length > 0)
        {
            // A path may sit inside a root as a string yet resolve, through a symbolic link or junction, to somewhere
            // outside it. The string check alone would let a planted link turn a "shared" path into an escape: follow
            // the links that exist and judge the real destination instead. Both sides of the comparison are in the
            // fully resolved spelling — the roots are put into it once at construction (see the constructor) — so a
            // root that itself lives behind a link still matches the paths beneath it. Without that, a genuinely
            // shared folder is refused as "not shared" (macOS keeps /var as a link to /private/var, and every
            // temporary path begins there).
            if (!_allowedRoots.Any(root => IsSameOrInside(resolved, root)))
            {
                throw new FileOperationException(FileError.NotAllowed, "That location is not shared.");
            }
        }

        return full;
    }

    /// <summary>
    /// The place a path actually leads once symbolic links and junctions are followed. Only the parts that exist are
    /// resolved (a path that is about to be created has nothing to follow yet); a nonexistent tail keeps its literal
    /// spelling, which the caller then checks against the roots as a string.
    /// </summary>
    /// <remarks>
    /// <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> follows a single reparse chain; walking the segments from
    /// the root down resolves a link inside a link. The walk stops at the first segment that does not exist, so a path
    /// into a directory that is still to be made is judged by the ancestor that is really there.
    /// <para>
    /// The target of a link is walked as well, and that is not redundant: a target's own ancestors may be links too.
    /// macOS keeps <c>/var</c> as a link to <c>/private/var</c> and every temporary path begins there, so a link
    /// inside the shared root hands back its target spelled <c>/var/…</c> while the root itself is spelled
    /// <c>/private/var/…</c>. Taking the target verbatim leaves the two sides of the comparison in different
    /// spellings of the same folder and a genuinely shared path is refused as not shared — which is exactly what a
    /// link pointing back inside the root must not be. Walking the target puts both sides in the fully resolved
    /// spelling. The walk is depth-bounded, so a cycle of links (which the operating system refuses to follow
    /// either, with ELOOP) ends the walk instead of hanging it.
    /// </para>
    /// </remarks>
    private static string ResolveFinalTarget(string full, int depth = 0)
    {
        if (depth >= MaxLinkDepth)
        {
            // A cycle of links cannot be followed by the operating system either (ELOOP), so no read or write can
            // land through one. Judging the spelling in hand is as close to an answer as exists, and it keeps the
            // walk finite; an escape through a cycle is still caught, because the cycle has to point at the outside
            // spelling to matter and that spelling resolves normally.
            return full;
        }

        var root = Path.GetPathRoot(full);
        if (root is null)
        {
            return full;
        }

        var current = root;
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);

            // A reparse point is only visible on a path segment that exists; a segment that does not exist yet (a file
            // or folder that is about to be created) cannot be followed, so the remaining tail stays literal.
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (!info.Exists)
            {
                current = candidate;
                continue;
            }

            var target = ReadLinkTarget(info);
            if (target is null)
            {
                current = candidate;
                continue;
            }

            // Follow the link, then walk the target itself so its own ancestors are resolved before the descent
            // continues. (ResolveLinkTarget already returns an absolute path even for a relative link, spelled
            // against the link's directory — verified on this runtime.)
            current = ResolveFinalTarget(target.FullName, depth + 1);
        }

        return TrimTrailingSeparator(current);
    }

    /// <summary>
    /// The link a filesystem entry points at, or <c>null</c> when it is not a link, does not exist yet, or cannot be
    /// read. The last case is the interesting one: the operating system follows a whole chain itself
    /// (<c>returnFinalTarget: true</c>) and answers ELOOP when the chain is a cycle, so a planted loop surfaces as an
    /// <see cref="IOException"/> rather than as an answer. A cycle has no exit by construction — every hop is a link
    /// with a single target — so an escape can never hide behind one, and no read or write can land through it
    /// either (the operating system refuses the I/O just as it refused the walk). Reporting "not a link" therefore
    /// costs nothing a security check should care about, keeps the raw ELOOP out of the protocol layer, and treats a
    /// link the walk cannot inspect exactly like the nonexistent tail above.
    /// </summary>
    private static FileSystemInfo? ReadLinkTarget(FileSystemInfo info)
    {
        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Validates the name of a file or folder to create or rename to (a single path segment).</summary>
    /// <exception cref="FileOperationException"><see cref="FileError.InvalidPath"/>.</exception>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name is "." or ".." ||
            name.AsSpan().ContainsAny(InvalidNameCharacters) || name.Any(char.IsControl) || IsReserved(name))
        {
            throw Invalid($"'{name}' is not a valid file name.");
        }

        // A trailing dot or space is only a problem on Windows, where the filesystem silently drops it and "a.txt."
        // then cannot be opened under the name it was created with. Unix keeps both characters, so a name ending in
        // one is a name the user meant.
        if (IsWindows && (name.EndsWith('.') || name.EndsWith(' ')))
        {
            throw Invalid($"'{name}' is not a valid file name.");
        }

        return name;
    }

    /// <summary>Whether <paramref name="path"/> is one of the configured roots or a drive root: things that must not be deleted or renamed.</summary>
    public bool IsProtectedRoot(string normalizedPath)
    {
        // The roots are stored in the fully resolved spelling (see the constructor) while the path arrives in the
        // caller's; resolving this side the same way puts the two in one spelling, so a root reached through a link is
        // still recognised when it is named by either its link or its target.
        var resolved = TryResolveSpelling(normalizedPath);
        return _allowedRoots.Any(root => string.Equals(root, resolved, PathComparison)) ||
            string.Equals(Path.GetPathRoot(resolved), resolved, PathComparison);
    }

    /// <summary>
    /// Windows device names. Nothing is reserved on Unix — "CON" and "NUL" there are ordinary file names, and the
    /// nearest equivalent (a device node under <c>/dev</c>) is already covered by <see cref="UnixKernelPaths"/>.
    /// </summary>
    private static bool IsReserved(string segment)
    {
        if (!IsWindows || segment.Length == 0)
        {
            return false;
        }

        // "CON", "con.txt" and "CON .txt" all open the device on Windows.
        var stem = segment.Split('.')[0].TrimEnd();
        return ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The path in the same resolved spelling the roots are stored in; a path that cannot be resolved is left as it is.</summary>
    private static string TryResolveSpelling(string path)
    {
        try
        {
            return ResolveFinalTarget(TrimTrailingSeparator(Path.GetFullPath(path)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool IsSameOrInside(string path, string root) =>
        path.Equals(root, PathComparison) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static string TrimTrailingSeparator(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }

    private static FileOperationException Invalid(string message) => new(FileError.InvalidPath, message);
}
