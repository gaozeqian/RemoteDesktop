using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Core.UnitTests;

public sealed class FileAccessPolicyTests : IDisposable
{
    private const string WindowsOnly = "Windows path syntax; run the Windows leg to cover it.";
    private const string UnixOnly = "Posix path syntax; run a Linux or macOS leg to cover it.";

    private static readonly FileAccessPolicy Open = FileAccessPolicy.Unrestricted;

    private readonly List<string> _temporary = [];

    /// <summary>
    /// Every directory the cases below create is real filesystem state under the temp folder; one run without this
    /// leaves a dozen of them behind.
    /// </summary>
    public void Dispose()
    {
        foreach (var path in _temporary)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // A handle the case under test still holds; the OS reclaims the folder later.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private string TempDirectory(string prefix)
    {
        var path = Directory.CreateTempSubdirectory(prefix).FullName;
        _temporary.Add(path);
        return path;
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\dir\\file\0.txt")]
    public void An_empty_or_malformed_path_is_rejected(string path)
    {
        var ex = Assert.Throws<FileOperationException>(() => Open.Normalize(path));
        Assert.Equal(FileError.InvalidPath, ex.Error);
    }

    [Theory]
    [InlineData("relative\\path.txt")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("C:relative.txt")]
    [InlineData("\\\\.\\PhysicalDrive0")]
    [InlineData("\\\\?\\C:\\Windows")]
    [InlineData("C:\\dir\\file.txt:hidden")]
    [InlineData("C:\\dir\\file.txt::$DATA")]
    [InlineData("C:\\dir\\CON")]
    [InlineData("C:\\dir\\nul.txt")]
    [InlineData("C:\\dir\\com1.log")]
    public void Dangerous_or_relative_windows_paths_are_rejected(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var ex = Assert.Throws<FileOperationException>(() => Open.Normalize(path));
        Assert.Equal(FileError.InvalidPath, ex.Error);
    }

    // The forward-slash spellings reach the same devices once GetFullPath has canonicalized them, so the device check
    // cannot rely on the raw spelling alone. A configured UNC root must not change the answer either.
    [Theory]
    [InlineData("//./PhysicalDrive0")]
    [InlineData("//?/C:/Windows")]
    [InlineData("/\\./PhysicalDrive0")]
    [InlineData("\\\\?/C:\\Windows")]
    public void Device_paths_are_rejected_in_every_spelling_even_with_a_network_root_configured(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        foreach (var policy in new[] { Open, new FileAccessPolicy([@"\\configured.invalid\share"]) })
        {
            var ex = Assert.Throws<FileOperationException>(() => policy.Normalize(path));
            Assert.Equal(FileError.InvalidPath, ex.Error);
        }
    }

    // A root that is a filesystem root already ends in the separator; paths beneath it must still count as inside.
    [Fact]
    public void A_filesystem_root_configured_as_the_shared_root_allows_the_paths_beneath_it()
    {
        var folder = TempDirectory("rd-fsroot-");
        try
        {
            var root = Path.GetPathRoot(folder)!;
            var policy = new FileAccessPolicy([root]);

            Assert.Equal(Path.Combine(folder, "file.txt"), policy.Normalize(Path.Combine(folder, "file.txt")));
            Assert.Equal(folder, policy.Normalize(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("relative/path.txt")]
    [InlineData("../etc/passwd")]
    [InlineData("./here")]
    // Kernel interfaces: /proc/kcore and /proc/self/mem read process memory, /dev/sda is the raw disk. With no roots
    // configured these would otherwise be reachable from the ordinary file browser.
    [InlineData("/proc/kcore")]
    [InlineData("/proc/self/mem")]
    [InlineData("/sys/kernel/uevent_seqnum")]
    [InlineData("/dev/sda")]
    public void Dangerous_or_relative_posix_paths_are_rejected(string path)
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);
        var ex = Assert.Throws<FileOperationException>(() => Open.Normalize(path));
        Assert.Equal(FileError.InvalidPath, ex.Error);
    }

    [Fact]
    public void A_colon_in_a_posix_path_is_an_ordinary_character()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);
        // The alternate-data-stream rule is a Windows idea. Applied on Unix it refused every path containing a colon,
        // which is a perfectly legal character there.
        Assert.Equal("/tmp/notes:2026.txt", Open.Normalize("/tmp/notes:2026.txt"));
    }

    [Fact]
    public void A_backslash_in_a_posix_path_is_an_ordinary_character()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);
        Assert.Equal("/tmp/back\\slash", Open.Normalize("/tmp/back\\slash"));
    }

    [Fact]
    public void Dot_segments_are_resolved_before_anything_else_is_decided()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        Assert.Equal(@"C:\a\c", Open.Normalize(@"C:\a\b\..\c"));
        Assert.Equal(@"C:\a", Open.Normalize(@"C:\a\"));
        Assert.Equal(@"C:\", Open.Normalize(@"C:\"));
    }

    [Fact]
    public void Dot_segments_are_resolved_before_anything_else_is_decided_posix()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);
        Assert.Equal("/home/u/c", Open.Normalize("/home/u/b/../c"));
        Assert.Equal("/home/u", Open.Normalize("/home/u/"));
        Assert.Equal("/", Open.Normalize("/"));
    }

    [Fact]
    public void A_configured_root_confines_every_path_including_tricks_that_climb_out_of_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var policy = new FileAccessPolicy([@"C:\Share"]);

        Assert.Equal(@"C:\Share", policy.Normalize(@"C:\Share"));
        // Windows paths are case-insensitive, so the root check is too; the casing itself is left as given.
        Assert.Equal(@"c:\share\sub\f.txt", policy.Normalize(@"c:\share\sub\f.txt"));
        Assert.Equal(@"C:\Share\x", policy.Normalize(@"C:\Share\sub\..\x"));

        foreach (var outside in new[] { @"C:\", @"C:\Share\..", @"C:\Share\..\Other", @"C:\ShareEvil", @"C:\Share\..\ShareEvil\f.txt", @"D:\Share" })
        {
            var ex = Assert.Throws<FileOperationException>(() => policy.Normalize(outside));
            Assert.Equal(FileError.NotAllowed, ex.Error);
        }
    }

    [Fact]
    public void A_configured_root_confines_every_posix_path_including_tricks_that_climb_out_of_it()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);
        var root = TempDirectory("rd-root-");
        var policy = new FileAccessPolicy([root]);

        Assert.Equal(Path.Combine(root, "sub", "f.txt"), policy.Normalize(Path.Combine(root, "sub", "f.txt")));
        Assert.Equal(Path.Combine(root, "x"), policy.Normalize(Path.Combine(root, "sub", "..", "x")));

        foreach (var outside in new[] { "/", root + "/..", root + "/../Other", root + "Evil", root + "/../" + Path.GetFileName(root) + "Evil/f.txt" })
        {
            var ex = Assert.Throws<FileOperationException>(() => policy.Normalize(outside));
            Assert.Equal(FileError.NotAllowed, ex.Error);
        }

        // A directory whose name merely starts with the root's name is outside it, and on a case-sensitive filesystem
        // so is one that differs only in case.
        var sibling = Directory.CreateDirectory(root + "Evil").FullName;
        Assert.Throws<FileOperationException>(() => policy.Normalize(Path.Combine(sibling, "f.txt")));
        Directory.Delete(sibling);

        var upper = new FileAccessPolicy([root.ToUpperInvariant()]);
        if (!string.Equals(root, root.ToUpperInvariant(), StringComparison.Ordinal))
        {
            Assert.Throws<FileOperationException>(() => upper.Normalize(Path.Combine(root, "f.txt")));
        }
    }

    [Fact]
    public void A_symbolic_link_out_of_a_root_is_refused()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);
        var outside = TempDirectory("rd-outside-");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");

        var root = TempDirectory("rd-link-root-");
        File.CreateSymbolicLink(Path.Combine(root, "escape"), outside);

        var policy = new FileAccessPolicy([root]);
        var ex = Assert.Throws<FileOperationException>(() => policy.Normalize(Path.Combine(root, "escape", "secret.txt")));
        Assert.Equal(FileError.NotAllowed, ex.Error);
    }

    /// <summary>
    /// The check that closes the check/use race is the one made on the opened handle, not the one made on its spelling
    /// before the open. This exercises that half directly with an outside file that a swapped link could have produced.
    /// </summary>
    [Fact]
    public void A_handle_that_really_points_outside_the_root_is_refused_after_the_open()
    {
        var outside = TempDirectory("rd-handle-outside-");
        var root = TempDirectory("rd-handle-root-");
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "x");

        var policy = new FileAccessPolicy([root]);
        using var opened = File.OpenRead(secret);

        var ex = Assert.Throws<FileOperationException>(() => policy.ValidateOpenedFile(opened.SafeFileHandle));
        Assert.Equal(FileError.NotAllowed, ex.Error);
    }

    /// <summary>
    /// A UNC path is refused from its spelling alone. Resolving it first would already ask the named server about the
    /// path (an SMB connection authenticated as the logged-in user), so the refusal has to come before any lookup.
    /// </summary>
    [Theory]
    [InlineData(@"\\attacker.invalid\share\x")]
    [InlineData(@"\\attacker.invalid\share\sub\..\x")]
    public void A_network_share_is_refused_without_looking_at_it(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var ex = Assert.Throws<FileOperationException>(() => Open.Normalize(path));
        Assert.Equal(FileError.NotAllowed, ex.Error);
        Assert.Contains("Network shares", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_network_share_other_than_the_configured_one_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        // The host names do not exist: resolving the configured root fails at once and keeps its spelling, and the
        // candidates below are refused from their spelling without a lookup.
        var policy = new FileAccessPolicy([@"\\configured.invalid\share"]);
        foreach (var other in new[] { @"\\attacker.invalid\share\x", @"\\configured.invalid\other\x" })
        {
            var ex = Assert.Throws<FileOperationException>(() => policy.Normalize(other));
            Assert.Equal(FileError.NotAllowed, ex.Error);
        }
    }

    [Fact]
    public void Creating_a_file_that_exists_says_so()
    {
        var root = TempDirectory("rd-exists-");
        try
        {
            var path = Path.Combine(root, "taken.txt");
            File.WriteAllText(path, "mine");

            var ex = Assert.Throws<FileOperationException>(() =>
                new FileAccessPolicy([root]).OpenWrite(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));

            // A caller that can pick another name needs to tell this apart from a failing disk.
            Assert.Equal(FileError.AlreadyExists, ex.Error);
            Assert.Equal("mine", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_verified_handle_can_be_read_after_the_path_is_gone()
    {
        var root = TempDirectory("rd-handle-inside-");
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "content");

        var policy = new FileAccessPolicy([root]);
        using var opened = policy.OpenRead(path);
        File.Delete(path);

        using var reader = new StreamReader(opened);
        Assert.Equal("content", reader.ReadToEnd());
    }

    [Theory]
    [InlineData("report.txt")]
    [InlineData("name with spaces.docx")]
    [InlineData("日本語.txt")]
    [InlineData(".hidden")]
    public void Ordinary_names_are_accepted(string name) => Assert.Equal(name, FileAccessPolicy.NormalizeName(name));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("tab\there")]
    public void Names_that_are_paths_or_unusual_are_rejected(string name)
    {
        var ex = Assert.Throws<FileOperationException>(() => FileAccessPolicy.NormalizeName(name));
        Assert.Equal(FileError.InvalidPath, ex.Error);
    }

    [Theory]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a|b")]
    [InlineData("aux")]
    [InlineData("aux.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void Windows_only_name_rules_are_applied_on_windows(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var ex = Assert.Throws<FileOperationException>(() => FileAccessPolicy.NormalizeName(name));
        Assert.Equal(FileError.InvalidPath, ex.Error);
    }

    [Theory]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a|b")]
    [InlineData("aux")]
    [InlineData("aux.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void Windows_only_name_rules_are_not_applied_elsewhere(string name)
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);
        // Every one of these is a name the filesystem accepts. Refusing them would make files that exist on disk
        // impossible to rename or create over the network.
        Assert.Equal(name, FileAccessPolicy.NormalizeName(name));
    }

    // Line breaks in a path are not refused by Normalize: on Unix they are ordinary characters of a legal file name, and
    // on Windows the path is accepted here and the operating system refuses it when the file is opened. This pins the
    // CURRENT behavior, it is not a statement that such paths are wanted. What keeps a path like this from forging a
    // log line is that log messages carry paths escaped, not that the policy turns them away.
    [Theory]
    [InlineData("a\nb.txt")]
    [InlineData("a\r\nb.txt")]
    [InlineData("file.txt\n")]
    public void A_line_break_in_a_path_is_passed_through_unchanged(string leaf)
    {
        var path = OperatingSystem.IsWindows() ? @"C:\dir\" + leaf : "/home/u/" + leaf;

        Assert.Equal(path, Open.Normalize(path));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("file.txt\n")]
    public void A_line_break_in_a_file_name_is_rejected(string name)
    {
        var ex = Assert.Throws<FileOperationException>(() => FileAccessPolicy.NormalizeName(name));
        Assert.Equal(FileError.InvalidPath, ex.Error);
    }

    [Fact]
    public void A_very_long_name_is_rejected() =>
        Assert.Throws<FileOperationException>(() => FileAccessPolicy.NormalizeName(new string('a', 256)));

    [Fact]
    public void Drive_roots_and_configured_roots_are_protected_from_deletion()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var policy = new FileAccessPolicy([@"C:\Share"]);
        Assert.True(policy.IsProtectedRoot(@"C:\"));
        Assert.True(policy.IsProtectedRoot(@"C:\Share"));
        Assert.False(policy.IsProtectedRoot(@"C:\Share\sub"));
    }

    [Fact]
    public void The_filesystem_root_and_configured_roots_are_protected_from_deletion()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);
        var root = TempDirectory("rd-root-");
        var policy = new FileAccessPolicy([root]);

        Assert.True(policy.IsProtectedRoot("/"));
        Assert.True(policy.IsProtectedRoot(root));
        Assert.False(policy.IsProtectedRoot(Path.Combine(root, "sub")));
    }
}

public sealed class LocalFileOperationsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rd-ops-").FullName;
    private readonly LocalFileOperations _ops = new(FileAccessPolicy.Unrestricted);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Listing_puts_folders_first_and_describes_entries()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zeta"));
        File.WriteAllBytes(Path.Combine(_root, "alpha.txt"), new byte[42]);
        File.WriteAllBytes(Path.Combine(_root, "Beta.txt"), []);
        File.SetAttributes(Path.Combine(_root, "Beta.txt"), FileAttributes.ReadOnly);

        var listing = _ops.List(_root);

        Assert.Equal(["zeta", "alpha.txt", "Beta.txt"], listing.Entries.Select(e => e.Name));
        Assert.Equal(42, listing.Entries[1].Size);
        Assert.Equal(FileEntryAttributes.ReadOnly, listing.Entries[2].Attributes);
        Assert.Equal(Path.GetDirectoryName(_root), listing.Parent);
        File.SetAttributes(Path.Combine(_root, "Beta.txt"), FileAttributes.Normal);
    }

    [Fact]
    public void A_hidden_entry_is_reported_as_hidden()
    {
        // Windows hides by attribute and Unix by a leading dot, so the name has to carry it there — setting the
        // attribute on Unix does not make the file hidden. The listing is what has to translate whichever applies.
        var name = OperatingSystem.IsWindows() ? "hidden.txt" : ".hidden.txt";
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, []);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.Hidden);
        }

        var entry = Assert.Single(_ops.List(_root).Entries);
        Assert.True(entry.Attributes.HasFlag(FileEntryAttributes.Hidden));
    }

    [Fact]
    public void An_empty_path_lists_the_drives_and_places_offer_starting_points()
    {
        var drives = _ops.List(string.Empty);
        Assert.NotEmpty(drives.Entries);
        Assert.All(drives.Entries, d => Assert.True(d.IsDirectory));
        Assert.Contains(_ops.GetPlaces(), p => p.IsDrive);
    }

    [Fact]
    public void Listing_a_file_or_a_missing_folder_says_why_not()
    {
        File.WriteAllText(Path.Combine(_root, "f.txt"), "x");
        Assert.Equal(FileError.InvalidPath, Assert.Throws<FileOperationException>(() => _ops.List(Path.Combine(_root, "f.txt"))).Error);
        Assert.Equal(FileError.NotFound, Assert.Throws<FileOperationException>(() => _ops.List(Path.Combine(_root, "nope"))).Error);
    }

    [Fact]
    public void Folders_can_be_created_renamed_and_deleted()
    {
        var folder = Path.Combine(_root, "new");
        _ops.MakeDirectory(folder);
        Assert.Equal(FileError.AlreadyExists, Assert.Throws<FileOperationException>(() => _ops.MakeDirectory(folder)).Error);

        File.WriteAllText(Path.Combine(folder, "f.txt"), "x");
        _ops.Rename(folder, "renamed");
        Assert.True(File.Exists(Path.Combine(_root, "renamed", "f.txt")));
        Assert.Equal(FileError.AlreadyExists, Assert.Throws<FileOperationException>(() => _ops.Rename(Path.Combine(_root, "renamed"), "renamed")).Error);
        // A rename target is one name, never a path: it is the separator inside it that the check is for, and which
        // character that is depends on the platform. A trailing backslash is a legal name character on Unix.
        var escaping = OperatingSystem.IsWindows() ? @"..\escaped" : "../escaped";
        Assert.Equal(FileError.InvalidPath, Assert.Throws<FileOperationException>(() => _ops.Rename(Path.Combine(_root, "renamed"), escaping)).Error);

        // A non-empty folder only goes with an explicit recursive request.
        Assert.Throws<FileOperationException>(() => _ops.Delete(Path.Combine(_root, "renamed"), recursive: false));
        Assert.True(Directory.Exists(Path.Combine(_root, "renamed")));
        _ops.Delete(Path.Combine(_root, "renamed"), recursive: true);
        Assert.False(Directory.Exists(Path.Combine(_root, "renamed")));
        Assert.Equal(FileError.NotFound, Assert.Throws<FileOperationException>(() => _ops.Delete(Path.Combine(_root, "renamed"), recursive: true)).Error);
    }

    [Fact]
    public void Drive_roots_cannot_be_deleted_or_renamed()
    {
        var root = Path.GetPathRoot(_root)!;
        Assert.Equal(FileError.NotAllowed, Assert.Throws<FileOperationException>(() => _ops.Delete(root, recursive: true)).Error);
        Assert.Equal(FileError.NotAllowed, Assert.Throws<FileOperationException>(() => _ops.Rename(root, "x")).Error);
    }

    [Fact]
    public void A_policy_with_roots_also_limits_operations_and_places()
    {
        // Runs on every OS now: FileAccessPolicy resolves configured roots the same way it resolves the incoming path,
        // so a root that sits behind a symlink (macOS keeps /var as a symlink to /private/var, and every temp path
        // begins there) still matches the paths beneath it. Before P7 the unresolved root never matched the
        // link-resolved path and a genuinely shared folder was refused as "not shared".

        var inside = Directory.CreateDirectory(Path.Combine(_root, "inside")).FullName;
        var ops = new LocalFileOperations(new FileAccessPolicy([inside]));

        Assert.Equal(FileError.NotAllowed, Assert.Throws<FileOperationException>(() => ops.List(_root)).Error);
        Assert.Equal(FileError.NotAllowed, Assert.Throws<FileOperationException>(() => ops.MakeDirectory(Path.Combine(_root, "x"))).Error);
        Assert.Equal(FileError.NotAllowed, Assert.Throws<FileOperationException>(() => ops.Delete(inside, recursive: true)).Error); // the root itself
        ops.MakeDirectory(Path.Combine(inside, "x"));
        Assert.All(ops.GetPlaces(), p => Assert.StartsWith(inside, p.Path, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class PartialFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rd-part-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_partial_belongs_to_one_version_of_a_file()
    {
        var time = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var a = PartialFile.PathFor(@"C:\x\f.bin", 100, time);

        Assert.EndsWith(PartialFile.Extension, a, StringComparison.Ordinal);
        Assert.NotEqual(a, PartialFile.PathFor(@"C:\x\f.bin", 101, time));
        Assert.NotEqual(a, PartialFile.PathFor(@"C:\x\f.bin", 100, time.AddMilliseconds(1)));
        Assert.Equal(a, PartialFile.PathFor(@"C:\x\f.bin", 100, time));
    }

    [Fact]
    public void A_name_that_fits_keeps_its_partial_name_exactly()
    {
        var time = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(_root, new string('a', 200) + ".bin");

        // Partials written by earlier versions have to stay resumable, so a name that fits is never rewritten.
        Assert.Equal($"{path}.100-{time.ToUnixTimeMilliseconds():x}{PartialFile.Extension}", PartialFile.PathFor(path, 100, time));
    }

    [Fact]
    public void A_name_too_long_for_the_suffix_gets_a_shortened_but_stable_partial_name()
    {
        var time = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var first = Path.Combine(_root, new string('a', 255));
        var second = Path.Combine(_root, new string('a', 254) + "b");

        var partial = PartialFile.PathFor(first, long.MaxValue, time);

        Assert.True(Path.GetFileName(partial).Length <= 255, "The partial's own name must be a name the filesystem accepts.");
        Assert.Equal(_root, Path.GetDirectoryName(partial));
        Assert.EndsWith($".{long.MaxValue}-{time.ToUnixTimeMilliseconds():x}{PartialFile.Extension}", partial, StringComparison.Ordinal);
        Assert.Equal(partial, PartialFile.PathFor(first, long.MaxValue, time));
        Assert.NotEqual(partial, PartialFile.PathFor(second, long.MaxValue, time));

        // The result can really be created.
        File.WriteAllText(partial, "x");
        Assert.True(File.Exists(partial));
    }

    [Fact]
    public void Shortening_a_partial_name_never_cuts_a_surrogate_pair()
    {
        var time = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(_root, string.Concat(Enumerable.Repeat("\U0001F600", 120)));

        // A size with one more digit moves the cut by one character, so one of the two lands inside a pair.
        foreach (var size in new[] { 1L, 10L })
        {
            var name = Path.GetFileName(PartialFile.PathFor(path, size, time));

            Assert.True(name.Length <= 255);
            for (var i = 0; i < name.Length; i++)
            {
                Assert.Equal(char.IsHighSurrogate(name[i]), i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]));
            }
        }
    }

    [Fact]
    public void Free_names_count_up_without_touching_what_exists()
    {
        var path = Path.Combine(_root, "doc.txt");
        Assert.Equal(path, PartialFile.FreeName(path));

        File.WriteAllText(path, "");
        File.WriteAllText(Path.Combine(_root, "doc (1).txt"), "");
        Directory.CreateDirectory(Path.Combine(_root, "doc (2).txt"));
        Assert.Equal(Path.Combine(_root, "doc (3).txt"), PartialFile.FreeName(path));
    }
}

public sealed class RemoteFileSourceCombineTests
{
    [Theory]
    [InlineData("C:\\Share", "file.txt", "C:\\Share\\file.txt")]
    [InlineData("C:\\Share\\", "file.txt", "C:\\Share\\file.txt")]
    [InlineData("/Volumes/Data", "file.txt", "/Volumes/Data/file.txt")]
    [InlineData("/Volumes/Data/", "file.txt", "/Volumes/Data/file.txt")]
    public void Combine_keeps_the_separator_of_the_remote_folder(string folder, string name, string expected) =>
        Assert.Equal(expected, RemoteFileSource.CombinePaths(folder, name));

    [Fact]
    public void Combine_empty_folder_returns_the_name() =>
        Assert.Equal("report.txt", RemoteFileSource.CombinePaths(string.Empty, "report.txt"));

    [Fact]
    public void Combine_without_a_separator_in_the_folder_uses_the_local_OS_separator()
    {
        // A bare drive root like "C:" carries no separator; fall back to the local OS convention. Either slash is
        // tolerated by the host's own path parsing, so only "the local separator" is asserted.
        Assert.Equal($"C:{Path.DirectorySeparatorChar}Users", RemoteFileSource.CombinePaths("C:", "Users"));
    }
}
