using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using RemoteDesktop.Abstractions.Agent;

namespace RemoteDesktop.Platform.Windows.Agent;

/// <summary>Nobody is logged on to the console session (login screen): only the secure desktop exists.</summary>
internal sealed class NoUserLoggedOnException : Exception
{
    public NoUserLoggedOnException()
        : base("No user is logged on at the console.")
    {
    }

    public NoUserLoggedOnException(string message)
        : base(message)
    {
    }

    public NoUserLoggedOnException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Everything needed to start an agent on one desktop of one session. Held between preparing and starting so the
/// agent pipe can be created with the right access rules before the process exists.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PreparedLaunch(DesktopKind desktop, uint sessionId, nint primaryToken, nint environment, SecurityIdentifier? user) : IDisposable
{
    public DesktopKind Desktop { get; } = desktop;

    public uint SessionId { get; } = sessionId;

    /// <summary>Primary token to start the process with; 0 when the launcher runs the agent as the current user.</summary>
    public nint PrimaryToken { get; } = primaryToken;

    public nint Environment { get; } = environment;

    /// <summary>The account the agent runs as, which must be allowed on its pipe; null for SYSTEM.</summary>
    public SecurityIdentifier? User { get; } = user;

    public void Dispose() => SessionProcessLauncher.Release(this);
}

/// <summary>
/// Starts the agent in the interactive session. As a service (LocalSystem) it duplicates a token of the right account:
/// the logged-in user's for their desktop, winlogon's (SYSTEM) for the login screen, lock screen and UAC prompts,
/// which no user process may even look at. Run as an ordinary user (development), it just starts the agent as that
/// user, which covers the user desktop only.
/// </summary>
/// <remarks>
/// The agent asks for UIAccess in its manifest, and that decides how it has to be started: the CreateProcess family
/// cannot *grant* UIAccess. An image whose manifest asks for it fails with ERROR_ELEVATION_REQUIRED (740) unless the
/// token it is created with already carries the flag - only the shell grants it, through the Application Information
/// service and only inside an interactive session, which is no use to a service in session 0. So the service puts the
/// flag on the duplicated token itself (<see cref="EnableUIAccess"/>, which LocalSystem may do), and the development
/// path goes through the shell.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class SessionProcessLauncher
{
    public const uint NoSession = 0xFFFFFFFF;

    private const int ErrorNoToken = 1008;
    private const uint MaximumAllowed = 0x02000000;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    public static bool IsLocalSystem { get; } = WindowsIdentity.GetCurrent().IsSystem;

    public static uint GetActiveSessionId() => WTSGetActiveConsoleSessionId();

    /// <exception cref="NoUserLoggedOnException">The user desktop was requested but nobody is logged on.</exception>
    /// <exception cref="AgentUnavailableException">The requested desktop cannot be reached with the current privileges.</exception>
    public static PreparedLaunch Prepare(DesktopKind desktop, uint sessionId)
    {
        if (!IsLocalSystem)
        {
            if (desktop == DesktopKind.Secure)
            {
                throw new AgentUnavailableException("The login screen and UAC prompts can only be reached through the installed background service.");
            }

            if (sessionId != (uint)Process.GetCurrentProcess().SessionId)
            {
                throw new AgentUnavailableException("The console belongs to another session; run the host as a service to follow it.");
            }

            return new PreparedLaunch(desktop, sessionId, 0, 0, WindowsIdentity.GetCurrent().User);
        }

        nint source = 0;
        nint primary = 0;
        nint environment = 0;
        try
        {
            if (desktop == DesktopKind.User)
            {
                if (!WTSQueryUserToken(sessionId, out source))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw error == ErrorNoToken ? new NoUserLoggedOnException() : new Win32Exception(error);
                }
            }
            else
            {
                source = OpenWinlogonToken(sessionId);
            }

            if (!DuplicateTokenEx(source, MaximumAllowed, 0, SecurityImpersonation, TokenPrimary, out primary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            // Before anything is started with this token: without the flag the agent cannot be created at all, because
            // its manifest asks for UIAccess and CreateProcessAsUser does not grant that (see the remarks on the class).
            if (!EnableUIAccess(primary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!CreateEnvironmentBlock(out environment, primary, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            using var identity = new WindowsIdentity(primary);
            var prepared = new PreparedLaunch(desktop, sessionId, primary, environment, desktop == DesktopKind.User ? identity.User : null);
            primary = 0;
            environment = 0;
            return prepared;
        }
        finally
        {
            if (source != 0)
            {
                CloseHandle(source);
            }

            if (primary != 0)
            {
                CloseHandle(primary);
            }

            if (environment != 0)
            {
                DestroyEnvironmentBlock(environment);
            }
        }
    }

    public static Process Start(PreparedLaunch launch, string executable, string arguments)
    {
        ArgumentNullException.ThrowIfNull(launch);

        var directory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;
        if (launch.PrimaryToken == 0)
        {
            // Through the shell rather than CreateProcess, for the reason the class remarks give: the shell is what can
            // grant the UIAccess the agent asks for. It only ever knows about the session it runs in, which is exactly
            // what this development path wants - the session the person running the host is sitting at.
            return Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = directory,
            }) ?? throw new AgentUnavailableException("The agent process could not be started.");
        }

        var desktopName = Marshal.StringToHGlobalUni(launch.Desktop == DesktopKind.User ? @"winsta0\default" : @"winsta0\winlogon");
        var commandLine = Marshal.StringToHGlobalUni($"\"{executable}\" {arguments}");
        try
        {
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = desktopName };
            if (!CreateProcessAsUser(launch.PrimaryToken, 0, commandLine, 0, 0, false, CreateUnicodeEnvironment | CreateNoWindow,
                    launch.Environment, directory, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return Process.GetProcessById(info.dwProcessId);
        }
        finally
        {
            Marshal.FreeHGlobal(desktopName);
            Marshal.FreeHGlobal(commandLine);
        }
    }

    internal static void Release(PreparedLaunch launch)
    {
        if (launch.PrimaryToken != 0)
        {
            CloseHandle(launch.PrimaryToken);
        }

        if (launch.Environment != 0)
        {
            DestroyEnvironmentBlock(launch.Environment);
        }
    }

    /// <summary>Puts the UIAccess flag on a token, which is what lets the agent be started at all (see the class remarks).</summary>
    /// <remarks>Requires SeTcbPrivilege, which the LocalSystem the service runs as holds.</remarks>
    private static bool EnableUIAccess(nint token)
    {
        var enabled = 1;
        return SetTokenInformation(token, TokenUIAccess, in enabled, sizeof(int));
    }

    private static nint OpenWinlogonToken(uint sessionId)
    {
        foreach (var winlogon in Process.GetProcessesByName("winlogon"))
        {
            using (winlogon)
            {
                if ((uint)winlogon.SessionId != sessionId)
                {
                    continue;
                }

                var process = OpenProcess(ProcessQueryInformation, false, winlogon.Id);
                if (process == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    return OpenProcessToken(process, TokenDuplicate | TokenQuery, out var token)
                        ? token
                        : throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally
                {
                    CloseHandle(process);
                }
            }
        }

        throw new AgentUnavailableException($"winlogon.exe of session {sessionId} not found.");
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint sessionId, out nint token);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(nint existing, uint access, nint attributes, int impersonationLevel, int tokenType, out nint newToken);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    // TokenUIAccess from TOKEN_INFORMATION_CLASS; SetTokenInformation takes a BOOL for it.
    private const int TokenUIAccess = 26;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(nint token, int tokenInformationClass, in int tokenInformation, int tokenInformationLength);

    // lpCommandLine must be writable memory, hence raw pointers for the strings.
    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUser(
        nint token,
        nint applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
