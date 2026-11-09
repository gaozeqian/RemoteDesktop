using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteDesktop.Application.Hosting;

/// <summary>Authenticates the Windows process on the server side of a host-control pipe.</summary>
internal static class WindowsPipeServerIdentity
{
    public static void EnsureExpected(NamedPipeClientStream pipe, string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the host-control server process.");
        }

        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the host-control server process.");
        }

        var capacity = 1024u;
        var path = new char[capacity];
        if (!QueryFullProcessImageName(process, 0, path, ref capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the host-control server executable.");
        }

        var actualPath = new string(path, 0, (int)capacity);
        static string Normalize(string value)
        {
            var full = Path.GetFullPath(value);
            return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full[4..] : full;
        }

        if (!string.Equals(Normalize(actualPath), Normalize(expectedPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"The host-control pipe belongs to an unexpected server process. Actual: '{actualPath}'; expected: '{expectedPath}'.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] path, ref uint capacity);
}
