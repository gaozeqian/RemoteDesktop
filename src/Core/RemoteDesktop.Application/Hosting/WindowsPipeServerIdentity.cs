using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteDesktop.Application.Hosting;

/// <summary>Authenticates the Windows process on the server side of a host-control pipe.</summary>
internal static class WindowsPipeServerIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorAccessDenied = 5;

    public static void EnsureExpected(NamedPipeClientStream pipe, string expectedPath, string? serviceName)
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

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();

            // In an installed setup the pipe belongs to the RemoteDesktopHost service, which runs as LocalSystem,
            // and the window asking is the signed-in person's: a standard user gets access denied even for
            // PROCESS_QUERY_LIMITED_INFORMATION, because a service process's DACL grants that to SYSTEM and
            // administrators only. The service control manager answers the same two questions - which process the
            // service is running as, and which executable it was started from - to any interactive user, and only
            // an administrator can change either answer. So the service is asked when the process cannot be opened.
            if (error == ErrorAccessDenied)
            {
                EnsureExpectedService(processId, expectedPath, serviceName);
                return;
            }

            throw new Win32Exception(error, "Could not open the host-control server process.");
        }

        var capacity = 1024u;
        var path = new char[capacity];
        if (!QueryFullProcessImageName(process, 0, path, ref capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the host-control server executable.");
        }

        var actualPath = new string(path, 0, (int)capacity);
        if (!string.Equals(NormalizePath(actualPath), NormalizePath(expectedPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"The host-control pipe belongs to an unexpected server process. Actual: '{actualPath}'; expected: '{expectedPath}'.");
        }
    }

    /// <summary>
    /// The same check through the service control manager, for the server process this account may not open.
    /// </summary>
    /// <remarks>
    /// Both facts are needed, not one: the process id proves the pipe instance belongs to the process the SCM is
    /// running as the service (a process that took the pipe name for itself has a different id), and the stored
    /// executable proves that service is the one expected here rather than a service that was pointed elsewhere.
    /// </remarks>
    private static void EnsureExpectedService(uint processId, string expectedPath, string? serviceName)
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new UnauthorizedAccessException(
                "The host-control pipe belongs to a process this account cannot inspect, and no Windows service name was supplied to identify it.");
        }

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the service control manager.");
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                throw new UnauthorizedAccessException(
                    $"The host-control pipe belongs to a process this account cannot inspect, and the {serviceName} service it was expected to be could not be opened: {new Win32Exception(error).Message}");
            }

            try
            {
                var status = default(ServiceStatusProcess);
                if (!QueryServiceStatusEx(service, ServiceStatusProcessInfo, ref status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not read the state of the {serviceName} service.");
                }

                var mismatch = DescribeMismatch(
                    processId,
                    status.ProcessId,
                    ReadServiceCommandLine(service, serviceName),
                    expectedPath,
                    serviceName);
                if (mismatch is not null)
                {
                    throw new UnauthorizedAccessException(mismatch);
                }
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// The decision the service-control-manager path makes, with the interop taken out of it so the rules can be
    /// tested without a running service: the two process ids must be the same, and the executable the SCM starts for
    /// that service must be the one this client expects. A null answer means the pipe belongs to our service; anything
    /// else is the reason to refuse it.
    /// </summary>
    internal static string? DescribeMismatch(
        uint pipeProcessId,
        uint serviceProcessId,
        string serviceCommandLine,
        string expectedPath,
        string serviceName)
    {
        if (serviceProcessId == 0 || serviceProcessId != pipeProcessId)
        {
            return $"The host-control pipe belongs to an unexpected server process. Actual: process {pipeProcessId}; "
                   + $"expected: the {serviceName} service, which is process {serviceProcessId}.";
        }

        var actualPath = ServiceExecutable(serviceCommandLine);
        return string.Equals(NormalizePath(actualPath), NormalizePath(expectedPath), StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The host-control pipe belongs to an unexpected server process. Actual: '{actualPath}'; expected: '{expectedPath}'.";
    }

    /// <summary>
    /// Only the executable from an SCM command line: quoted when its path has spaces, and free to carry arguments or
    /// environment variables. The rest of the line must not be compared, or a service started with a switch would be
    /// mistaken for a different program.
    /// </summary>
    internal static string ServiceExecutable(string commandLine)
    {
        var expanded = Environment.ExpandEnvironmentVariables((commandLine ?? string.Empty).Trim());
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            return end > 0 ? expanded[1..end] : expanded[1..];
        }

        var argument = expanded.IndexOf(" -", StringComparison.Ordinal);
        return argument > 0 ? expanded[..argument] : expanded;
    }

    /// <summary>The command line the SCM starts for a service, taken from its configuration.</summary>
    private static string ReadServiceCommandLine(IntPtr service, string serviceName)
    {
        // The configuration is variable-sized - its strings follow the struct - so the size is asked for first and
        // the answer then read into a buffer of that size.
        _ = QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not read the executable of the {serviceName} service.");
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!QueryServiceConfig(service, buffer, needed, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not read the executable of the {serviceName} service.");
            }

            var config = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            return Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string NormalizePath(string value)
    {
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // A path the file system APIs refuse cannot be the expected executable; comparing it as it is says so.
            full = value;
        }

        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full[4..] : full;
    }

    private const uint ScManagerConnect = 0x0001;

    private const uint ServiceQueryConfig = 0x0001;

    private const uint ServiceQueryStatus = 0x0004;

    private const int ServiceStatusProcessInfo = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] path, ref uint capacity);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, ref ServiceStatusProcess status, int size, out int needed);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
