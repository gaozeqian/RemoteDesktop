using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Features.FileTransfer;

/// <summary>
/// Opens files under the same policy and then checks the target the operating system actually opened. The check
/// before the open is necessary but not sufficient: another local process can replace a path component with a link
/// in the interval. A handle cannot be redirected after it is opened, so validating it before reading or writing is
/// what closes that race for file content.
/// </summary>
public sealed partial class FileAccessPolicy
{
    private const int MacPathMax = 1024;
    private const int MacFGetPath = 50;

    /// <summary>Opens an existing file for reading and verifies the real target behind its handle.</summary>
    public FileStream OpenRead(string path) =>
        OpenVerified(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Opens a file for writing/creating and verifies the real target behind its handle.</summary>
    public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share) =>
        OpenVerified(path, mode, access, share);

    /// <summary>
    /// Throws unless the file behind <paramref name="handle"/> is still inside the configured roots and outside
    /// kernel interfaces. Exposed to tests so the post-open half of the race can be exercised directly.
    /// </summary>
    internal void ValidateOpenedFile(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new FileOperationException(FileError.Io, "The file could not be opened.");
        }

        var actual = FinalPathOf(handle) ?? throw new FileOperationException(
            FileError.NotAllowed,
            "The real destination of the opened file could not be verified.");

        if (!Path.IsPathFullyQualified(actual))
        {
            throw new FileOperationException(FileError.NotAllowed, "The real destination of the opened file is not absolute.");
        }

        EnsureLocationAllowed(TrimTrailingSeparator(Path.GetFullPath(actual)));
    }

    private FileStream OpenVerified(string path, FileMode mode, FileAccess access, FileShare share)
    {
        var full = Normalize(path);
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(
                full,
                mode,
                access,
                share,
                FileOptions.Asynchronous | (access == FileAccess.Read ? FileOptions.SequentialScan : FileOptions.None));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileOperationException(FileError.AccessDenied, "Access to that location was denied.", ex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileOperationException(FileError.NotFound, "The file does not exist.", ex);
        }
        catch (IOException ex)
        {
            throw new FileOperationException(FileError.Io, $"The file cannot be opened: {ex.Message}", ex);
        }

        try
        {
            ValidateOpenedFile(handle);
            return new FileStream(handle, access, bufferSize: 1, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string? FinalPathOf(SafeFileHandle handle)
    {
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (IsWindows)
        {
            return WindowsFinalPath(handle);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxFinalPath(descriptor);
        }

        return OperatingSystem.IsMacOS() ? MacFinalPath(descriptor) : null;
    }

    private static string? WindowsFinalPath(SafeFileHandle handle)
    {
        for (var size = 512; size <= 32_768; size *= 2)
        {
            var buffer = new char[size];
            var written = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, flags: 0);
            if (written == 0)
            {
                throw new IOException("GetFinalPathNameByHandle failed.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (written < buffer.Length)
            {
                var path = new string(buffer, 0, (int)written);
                if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                {
                    return @"\\" + path[8..];
                }

                return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
            }
        }

        return null;
    }

    private static string? LinuxFinalPath(int descriptor)
    {
        try
        {
            var link = new FileInfo($"/proc/self/fd/{descriptor}");
            var target = link.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? MacFinalPath(int descriptor)
    {
        var buffer = new byte[MacPathMax];
        if (Fcntl(descriptor, MacFGetPath, buffer) != 0)
        {
            return null;
        }

        var end = Array.IndexOf(buffer, (byte)0);
        return end <= 0 ? null : Encoding.UTF8.GetString(buffer, 0, end);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command, [Out] byte[] buffer);
}
