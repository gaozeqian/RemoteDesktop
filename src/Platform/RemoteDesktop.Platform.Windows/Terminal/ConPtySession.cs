using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Abstractions.Terminal;

namespace RemoteDesktop.Platform.Windows.Terminal;

/// <summary>One shell attached to a pseudoconsole, inside a job object that ends it and everything it started.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ConPtySession : IPtySession
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int UseStdHandles = 0x00000100;
    private const nint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly nint _console;
    private readonly SafeFileHandle _process;
    private readonly SafeFileHandle _job;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _exitWait;
    private readonly ManualResetEvent _exitEvent;
    private int _consoleClosed;
    private int _disposed;

    private ConPtySession(nint console, SafeFileHandle process, SafeFileHandle job, FileStream input, FileStream output)
    {
        _console = console;
        _process = process;
        _job = job;
        _input = input;
        _output = output;

        // When the shell is gone the pseudoconsole is closed so that it flushes what is left and ends the output stream.
        _exitEvent = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false) };
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => OnExited(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public Stream Output => _output;

    public Stream Input => _input;

    public static ConPtySession Start(string commandLine, string workingDirectory, int columns, int rows)
    {
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        nint console = 0;
        SafeFileHandle? job = null;
        SafeFileHandle? process = null;
        nint attributes = 0;
        nint environment = 0;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, 0, 0) || !CreatePipe(out outputRead, out outputWrite, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var hr = CreatePseudoConsole(new Coord(Clamp(columns), Clamp(rows)), inputRead, outputWrite, 0, out console);
            if (hr != 0)
            {
                throw new Win32Exception(hr, "The pseudoconsole could not be created.");
            }

            job = CreateKillOnCloseJob();

            var size = nint.Zero;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size) ||
                !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, nint.Size, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            environment = Marshal.StringToHGlobalUni(BuildEnvironment());
            var startup = new StartupInfoEx
            {
                // Explicitly no standard handles: otherwise a host whose own stdio is redirected (the agent, a test run) hands
                // those to the shell, which then writes to them instead of to the pseudoconsole.
                Info = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = UseStdHandles },
                AttributeList = attributes,
            };

            if (!CreateProcessW(null, commandLine, 0, 0, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environment, workingDirectory, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The shell could not be started.");
            }

            using var thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            process = new SafeFileHandle(info.Process, ownsHandle: true);
            if (!AssignProcessToJobObject(job, process))
            {
                // Still usable; the pseudoconsole closing ends the shell. Only the extra guarantee for stray children is lost.
                _ = Marshal.GetLastWin32Error();
            }

            // The pseudoconsole now owns its ends of the pipes.
            inputRead.Dispose();
            outputWrite.Dispose();
            var session = new ConPtySession(
                console,
                process,
                job,
                new FileStream(inputWrite, FileAccess.Write, 1, isAsync: false),
                new FileStream(outputRead, FileAccess.Read, 1, isAsync: false));
            console = 0;
            job = null;
            process = null;
            inputWrite = null;
            outputRead = null;
            return session;
        }
        finally
        {
            if (environment != 0)
            {
                Marshal.FreeHGlobal(environment);
            }

            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (console != 0)
            {
                ClosePseudoConsole(console);
            }

            if (process is not null)
            {
                _ = TerminateProcess(process, 1);
                process.Dispose();
            }

            job?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
        }
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _consoleClosed) == 0)
        {
            _ = ResizePseudoConsole(_console, new Coord(Clamp(columns), Clamp(rows)));
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Ending the job kills the shell and whatever it started; then the console can be closed without waiting for anyone.
        _job.Dispose();
        _ = _exitWait.Unregister(null);
        CloseConsole();
        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
        _exitEvent.Dispose();
        _exited.TrySetResult(-1);
    }

    private void OnExited()
    {
        _ = GetExitCodeProcess(_process, out var code);
        _exited.TrySetResult((int)code);
        CloseConsole();
    }

    // Closing can block until the output has been drained (older Windows), so it never runs on the caller's thread.
    private void CloseConsole()
    {
        if (Interlocked.Exchange(ref _consoleClosed, 1) == 0)
        {
            _ = Task.Run(() => ClosePseudoConsole(_console));
        }
    }

    private static short Clamp(int value) => (short)Math.Clamp(value, 1, short.MaxValue);

    // The shell gets the user's environment, plus the terminal type that tools from Unix-like environments look for.
    private static string BuildEnvironment()
    {
        var variables = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        variables.TryAdd("TERM", "xterm-256color");
        variables["COLORTERM"] = "truecolor";
        var builder = new StringBuilder();
        foreach (var (key, value) in variables.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(key).Append('=').Append(value).Append('\0');
        }

        return builder.Append('\0').ToString();
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var limits = new JobObjectExtendedLimits { BasicLimitInformation = new JobObjectBasicLimits { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf<JobObjectExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved2Pointer;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Info;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimits
    {
        public JobObjectBasicLimits BasicLimitInformation;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, nint attributes, int size);

    [LibraryImport("kernel32.dll")]
    private static partial int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [LibraryImport("kernel32.dll")]
    private static partial int ResizePseudoConsole(nint console, Coord size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint console);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string? applicationName,
        string commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobObjectExtendedLimits information, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeFileHandle process, uint exitCode);
}
