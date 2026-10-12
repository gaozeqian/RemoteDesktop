using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.MacOS.Interop;

/// <summary>Terminal window size, as <c>struct winsize</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WinSize
{
    public ushort Rows;
    public ushort Columns;
    public ushort XPixel;
    public ushort YPixel;
}

/// <summary>
/// libc interop for the pseudo-terminal host.
/// </summary>
/// <remarks>
/// <para>
/// The shell is started with <c>posix_spawn</c> rather than the more common <c>forkpty</c>. Forking a managed process
/// leaves the child with a runtime that is only half there — one thread, and whatever locks the other threads held —
/// and the child is about to call into libc. <c>posix_spawn</c> gets the kernel to do the work instead, which is both
/// safe and the reason it exists. The controlling terminal is arranged the same way forkpty would have: the child is
/// made a session leader, then opens the replica without <c>O_NOCTTY</c>, so that open is what claims the terminal.
/// </para>
/// <para>
/// <b>Variadic libc functions are reached through their raw syscall stubs.</b> Apple's arm64 ABI passes variadic
/// arguments on the stack, but a P/Invoke signature has no way to say "variadic", so the marshaller puts the last
/// argument in a register where the callee will never look. Measured on this machine: the same pointer handed to a
/// plain function arrives intact, and handed to a variadic one arrives as an unrelated stack address — which is why
/// <c>ioctl</c> used to write the window size to a random address, and why reading it back crashed. Darwin keeps a
/// non-variadic stub for every one of these (<c>__ioctl</c>, <c>__open</c>), because libc's own variadic wrappers
/// are compiled to call it with the argument in a register. That fixes the ABI in place: the stubs cannot change
/// while <c>ioctl</c> still works, and they report failures through <c>errno</c> exactly as the wrappers do.
/// </para>
/// </remarks>
internal static unsafe partial class LibSystem
{
    // open() flags (Darwin values).
    public const int ORdWr = 0x0002;
    public const int ONoCtty = 0x20000;

    // posix_spawnattr flags.
    public const short SpawnSetsid = 0x0002;

    /// <summary><c>TIOCSWINSZ</c> on Darwin: <c>_IOW('t', 103, struct winsize)</c>.</summary>
    public const ulong Tiocswinsz = 0x8008_7467;

    /// <summary><c>TIOCGWINSZ</c> on Darwin: <c>_IOR('t', 104, struct winsize)</c>.</summary>
    public const ulong Tiocgwinsz = 0x4008_7468;

    public const int SignalHangup = 1;

    // ---- Pseudo-terminal allocation ---------------------------------------------------------------------------

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_openpt(int flags);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int grantpt(int fd);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int unlockpt(int fd);

    /// <summary>Returns a <c>char*</c> owned by libc; do not free it.</summary>
    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial byte* ptsname(int fd);

    /// <param name="path">The replica name from <see cref="ptsname"/>.</param>
    /// <param name="flags">Darwin's <c>O_*</c> values.</param>
    /// <param name="mode">Only read by the kernel when <c>O_CREAT</c> is set; ignored otherwise.</param>
    [LibraryImport(NativeLibraries.LibSystem, EntryPoint = "__open", SetLastError = true)]
    internal static partial int open(byte* path, int flags, int mode);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int close(int fd);

    /// <summary>Sets or reads the terminal window size, and nothing else.</summary>
    /// <remarks>
    /// Declared as taking a <see cref="WinSize"/> pointer because that is the only request this host issues. The
    /// entry point is the non-variadic stub rather than libc's <c>ioctl</c> for the reason given on the class.
    /// </remarks>
    [LibraryImport(NativeLibraries.LibSystem, EntryPoint = "__ioctl", SetLastError = true)]
    internal static partial int ioctl(int fd, ulong request, WinSize* argument);

    /// <summary>Applies a window size to a terminal descriptor.</summary>
    /// <remarks>
    /// Both ends of a pseudo-terminal accept this once a replica exists, so either descriptor will do. The size is
    /// clamped because <c>struct winsize</c> holds 16-bit fields and a resized window is an untrusted number.
    /// </remarks>
    internal static unsafe bool TrySetWindowSize(int descriptor, int columns, int rows)
    {
        var size = new WinSize
        {
            Columns = (ushort)Math.Clamp(columns, 1, ushort.MaxValue),
            Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
        };

        return ioctl(descriptor, Tiocswinsz, &size) == 0;
    }

    /// <summary>Reads a window size back from a terminal descriptor.</summary>
    /// <remarks>Reading it back is the only way to tell that a write landed, which is what the tests rely on.</remarks>
    internal static unsafe bool TryGetWindowSize(int descriptor, out int columns, out int rows)
    {
        var size = default(WinSize);
        var result = ioctl(descriptor, Tiocgwinsz, &size);
        columns = size.Columns;
        rows = size.Rows;
        return result == 0;
    }

    // ---- Process spawning -------------------------------------------------------------------------------------

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawn_file_actions_init(nint* fileActions);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawn_file_actions_destroy(nint* fileActions);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawn_file_actions_addopen(nint* fileActions, int fd, byte* path, int oflag, int mode);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawn_file_actions_adddup2(nint* fileActions, int fd, int newFd);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawn_file_actions_addclose(nint* fileActions, int fd);

    /// <summary>
    /// Sets the child's working directory as part of the spawn. Available since macOS 10.15 and the only way to do
    /// it without changing the parent's directory, which a background host must never do.
    /// </summary>
    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawn_file_actions_addchdir_np(nint* fileActions, byte* path);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawnattr_init(nint* attr);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawnattr_destroy(nint* attr);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawnattr_setflags(nint* attr, short flags);

    /// <param name="pid">Receives the child's <c>pid_t</c>, which is an <c>int</c> on Darwin.</param>
    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int posix_spawn(int* pid, byte* path, nint* fileActions, nint* attr, nint argv, nint envp);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int waitpid(int pid, int* status, int options);

    /// <summary>
    /// Signals a whole process group. The shell is made a session and process-group leader, so this is what reaches
    /// the shell and the jobs it kept in its own group.
    /// </summary>
    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int killpg(int pgrp, int signal);

    // ---- Raw descriptor I/O -----------------------------------------------------------------------------------

    /// <summary>What <c>poll</c> watches and reports for one descriptor, as <c>struct pollfd</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFileDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    /// <summary>There is something to read, or the terminal has been hung up.</summary>
    public const short PollIn = 0x0001;

    public const int ErrnoInterrupted = 4;

    /// <summary>Waits for one of the descriptors to become ready, or for the timeout to run out.</summary>
    /// <param name="timeout">Milliseconds, or -1 to wait indefinitely.</param>
    /// <remarks>
    /// This is what makes a terminal read cancellable. A plain <c>read</c> on a terminal parks the thread until the
    /// shell speaks, and a <see cref="CancellationToken"/> cannot reach a thread that is inside the kernel: measured
    /// here, a read with a twenty-second timeout was still parked well after it had fired. Waiting in short slices
    /// and looking at the token in between is what makes the token mean something.
    /// </remarks>
    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial int poll(PollFileDescriptor* descriptors, uint count, int timeout);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport(NativeLibraries.LibSystem, SetLastError = true)]
    internal static partial nint write(int fd, byte* buffer, nuint count);

    /// <summary>
    /// The process environment, which <c>posix_spawn</c> wants passed explicitly. <c>environ</c> is a global
    /// <c>char **</c> variable, so the symbol is looked up and dereferenced once.
    /// </summary>
    private static readonly nint EnvironSymbol = NativeLibrary.GetExport(
        NativeLibrary.Load(NativeLibraries.LibSystem), "environ");

    /// <summary>The current <c>char **environ</c>, or zero when it could not be read.</summary>
    public static unsafe nint Environ => EnvironSymbol == 0 ? 0 : *(nint*)EnvironSymbol;

    /// <summary>Reads the C string at <paramref name="value"/> into a managed string.</summary>
    public static unsafe string? ToManagedString(byte* value)
    {
        if (value is null)
        {
            return null;
        }

        var length = 0;
        while (value[length] != 0)
        {
            length++;
        }

        return System.Text.Encoding.UTF8.GetString(value, length);
    }
}
