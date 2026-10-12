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
