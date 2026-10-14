using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Core.UnitTests;

public sealed class FileAccessPolicyTests
{
    private const string WindowsOnly = "Windows path syntax.";
    private const string UnixOnly = "Posix path syntax.";

    private static readonly FileAccessPolicy Open = FileAccessPolicy.Unrestricted;

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
        var root = Directory.CreateTempSubdirectory("rd-root-").FullName;
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
        var outside = Directory.CreateTempSubdirectory("rd-outside-").FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");

        var root = Directory.CreateTempSubdirectory("rd-link-root-").FullName;
        File.CreateSymbolicLink(Path.Combine(root, "escape"), outside);

        var policy = new FileAccessPolicy([root]);
        var ex = Assert.Throws<FileOperationException>(() => policy.Normalize(Path.Combine(root, "escape", "secret.txt")));
        Assert.Equal(FileError.NotAllowed, ex.Error);
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
        var root = Directory.CreateTempSubdirectory("rd-root-").FullName;
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
