using System.Runtime.InteropServices;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// Adversarial checks for the file-access sandbox: paths that <em>look</em> inside a shared root but resolve, through a
/// symbolic link or junction, to somewhere outside it. <see cref="FileAccessPolicy.Normalize"/> follows reparse points
/// before judging the destination, so a link planted inside the root does not become an escape.
/// </summary>
/// <remarks>
/// These tests skip when a link cannot be created (a normal CI account has no privilege to make symlinks, and Windows
/// needs either elevation or Developer Mode). When they run, a failing test is a real sandbox escape, not a flake.
/// </remarks>
public sealed class SymbolicLinkEscapeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rd-link-root-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("rd-link-outside-").FullName;

    public void Dispose()
    {
        // Delete the link first, then the folders: deleting the root while it contains a junction must not follow it.
        TryDeleteDirectory(_root);
        TryDeleteDirectory(_outside);
    }

    [Fact]
    public void A_symlink_inside_the_root_cannot_escape_it()
    {
        var link = CreateDirectoryLink(Path.Combine(_root, "link"), _outside);
        if (link is null)
        {
            return; // no privilege to make a symlink; nothing to test here
        }

        var policy = new FileAccessPolicy([_root]);
        var secret = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(secret, "outside");

        // The path is a string inside the root, but it resolves through the link to the outside file: it must be refused.
        var ex = Assert.Throws<FileOperationException>(() => policy.Normalize(Path.Combine(_root, "link", "secret.txt")));
        Assert.Equal(FileError.NotAllowed, ex.Error);
    }

    [Fact]
    public void A_link_swapped_in_after_the_check_leaves_no_file_behind_outside_the_root()
    {
        var probe = CreateDirectoryLinkOrJunction(Path.Combine(_root, "probe"), _outside);
        if (probe is null)
        {
            return; // no privilege to make a symlink; nothing to test here
        }

        Directory.Delete(probe); // removes the link, not its target

        var folder = Path.Combine(_root, "folder");
        Directory.CreateDirectory(folder);
        var policy = new FileAccessPolicy([_root])
        {
            // The path has been judged inside the root; now the folder becomes a link to the outside, which is the
            // window a local process needs. The handle check refuses the file, and the empty file the open made out
            // there must not stay behind.
            AfterNormalizeForTests = () =>
            {
                Directory.Delete(folder);
                _ = CreateDirectoryLinkOrJunction(folder, _outside);
            },
        };

        var ex = Assert.Throws<FileOperationException>(() =>
            policy.OpenWrite(Path.Combine(folder, "new.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.None));

        Assert.Equal(FileError.NotAllowed, ex.Error);
        Assert.False(File.Exists(Path.Combine(_outside, "new.txt")), "The file created outside the root was left behind.");
    }

    [Fact]
    public void Listing_through_a_link_is_refused()
    {
        var link = CreateDirectoryLink(Path.Combine(_root, "link"), _outside);
        if (link is null)
        {
            return;
        }

        File.WriteAllText(Path.Combine(_outside, "outside.txt"), "x");

        var policy = new FileAccessPolicy([_root]);
        var operations = new LocalFileOperations(policy);

        var ex = Assert.Throws<FileOperationException>(() => operations.List(Path.Combine(_root, "link")));
        Assert.Equal(FileError.NotAllowed, ex.Error);
    }

    [Fact]
    public void Writing_through_a_link_is_refused()
    {
        var link = CreateDirectoryLink(Path.Combine(_root, "link"), _outside);
        if (link is null)
        {
            return;
        }

        var policy = new FileAccessPolicy([_root]);
        var operations = new LocalFileOperations(policy);

        var ex = Assert.Throws<FileOperationException>(() => operations.MakeDirectory(Path.Combine(_root, "link", "new-folder")));
        Assert.Equal(FileError.NotAllowed, ex.Error);

        Assert.False(Directory.Exists(Path.Combine(_outside, "new-folder")));
    }

    /// <summary>
    /// A link that points back inside the root is fine: only escapes are refused, not links themselves.
    /// </summary>
    [Fact]
    public void A_link_that_stays_inside_the_root_is_allowed()
    {
        var inside = Directory.CreateDirectory(Path.Combine(_root, "inside")).FullName;
        var link = CreateDirectoryLink(Path.Combine(_root, "link"), inside);
        if (link is null)
        {
            return;
        }

        var policy = new FileAccessPolicy([_root]);
        Assert.Equal(Path.Combine(_root, "link"), policy.Normalize(Path.Combine(_root, "link")));
    }

    /// <summary>
    /// A cycle of links must end the resolution walk rather than hang it. The operating system refuses to follow such
    /// a chain either (ELOOP), so nothing can actually be read through one; the policy still has to come back with an
    /// answer. What it answers with is only that the walk stayed finite and the sandbox held: a cycle that points at
    /// an outside spelling resolves there and is refused like any other escape.
    /// </summary>
    [Fact]
    public void A_cycle_of_links_does_not_hang_the_resolution()
    {
        var first = CreateDirectoryLink(Path.Combine(_root, "first"), Path.Combine(_root, "second"));
        var second = CreateDirectoryLink(Path.Combine(_root, "second"), Path.Combine(_root, "first"));
        if (first is null || second is null)
        {
            return;
        }

        var policy = new FileAccessPolicy([_root]);

        // Both spellings walk through the cycle; neither may hang, and neither may escape the root while doing so.
        _ = policy.Normalize(Path.Combine(_root, "first"));
        _ = policy.Normalize(Path.Combine(_root, "second"));
    }

    /// <summary>A symbolic link where one can be made, otherwise a junction (Windows makes those without elevation).</summary>
    private static string? CreateDirectoryLinkOrJunction(string link, string target)
    {
        if (CreateDirectoryLink(link, target) is { } made)
        {
            return made;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        process?.WaitForExit();
        return process?.ExitCode == 0 ? link : null;
    }

    /// <summary>
    /// Creates a directory symbolic link (or junction as a fallback) from <paramref name="link"/> to
    /// <paramref name="target"/>. Returns the link path when it was created, or <c>null</c> when the account cannot
    /// make one — in which case the caller skips the test rather than fail on the environment.
    /// </summary>
    private static string? CreateDirectoryLink(string link, string target)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(link, target);
            return link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows refuses without elevation or Developer Mode; a non-Windows platform may not support it at all.
            return null;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
