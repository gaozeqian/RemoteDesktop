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
    // struct vnode_fdinfowithpath from <sys/proc_info.h>: a proc_fileinfo (24 bytes), then a vnode_info (152 bytes), then
    // the path (MAXPATHLEN = 1024 bytes). The path therefore starts at offset 176 and the whole struct is 1200 bytes.
    private const int MacProcPidFdVnodePathInfo = 2;
    private const int MacVnodeInfoWithPathSize = 1200;
    private const int MacVnodePathOffset = 176;
    private const int MacPathMax = 1024;

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

    /// <summary>Runs after the path has passed <see cref="Normalize"/> and before it is opened: the window a link swap needs. Tests only.</summary>
    internal Action? AfterNormalizeForTests { get; set; }

    private FileStream OpenVerified(string path, FileMode mode, FileAccess access, FileShare share)
    {
        var full = Normalize(path);
        AfterNormalizeForTests?.Invoke();
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
        catch (IOException ex) when (mode == FileMode.CreateNew && (File.Exists(full) || Directory.Exists(full)))
        {
            // CreateNew failing on a name that is there means somebody else got it first. Callers that can pick another
            // name (a rename-on-conflict upload) need to tell this apart from a disk that is broken or full.
            throw new FileOperationException(FileError.AlreadyExists, "A file with that name already exists.", ex);
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
            // CreateNew and OpenOrCreate can both make a brand-new empty file. If the handle turned out to lead
            // outside the policy (a link was swapped in after Normalize), the empty file it made out there is ours to
            // take back. OpenOrCreate on an existing non-empty file answers null and is left alone.
            var stray = mode is FileMode.CreateNew or FileMode.OpenOrCreate ? EmptyFileLocation(handle) : null;
            handle.Dispose();
            if (stray is not null)
            {
                TryDelete(stray);
            }

            throw;
        }
    }

    /// <summary>Where the file behind <paramref name="handle"/> really is, if it is still empty; <c>null</c> when that cannot be told.</summary>
    private static string? EmptyFileLocation(SafeFileHandle handle)
    {
        try
        {
            return !handle.IsInvalid && !handle.IsClosed && RandomAccess.GetLength(handle) == 0 ? FinalPathOf(handle) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: an empty stray file is untidy, not a reason to hide the refusal that led here.
        }
    }

    private static string? FinalPathOf(SafeFileHandle handle)
    {
        if (IsWindows)
        {
            return WindowsFinalPath(handle);
        }

        // Only a Unix descriptor is a small integer; a Windows handle is a pointer-sized value that ToInt32 may refuse.
        var descriptor = handle.DangerousGetHandle().ToInt32();
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
        // fcntl(F_GETPATH) is not usable from here: fcntl is variadic in C, and on Apple Silicon variadic arguments are
        // passed on the stack rather than in registers. A P/Invoke declared with a fixed signature therefore hands the
        // kernel a garbage buffer address, and the call corrupts memory (an AccessViolationException that takes the
        // whole process down). proc_pidfdinfo has a fixed signature and returns the same path.
        var buffer = new byte[MacVnodeInfoWithPathSize];
        var written = ProcPidFdInfo(Environment.ProcessId, descriptor, MacProcPidFdVnodePathInfo, buffer, buffer.Length);
        if (written < MacVnodePathOffset + 1)
        {
            return null;
        }

        var path = buffer.AsSpan(MacVnodePathOffset, MacPathMax);
        var end = path.IndexOf((byte)0);
        return end <= 0 ? null : Encoding.UTF8.GetString(path[..end]);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    [DllImport("libproc", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    private static extern int ProcPidFdInfo(int pid, int descriptor, int flavor, [Out] byte[] buffer, int bufferSize);
}
