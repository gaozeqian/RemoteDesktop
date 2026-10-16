using System.Reflection;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.Posix.Interop;

/// <summary>
/// The C library entry points a Posix platform layer needs. Hand-written bindings, in the same style as the Windows
/// project's, rather than a package: the surface is small and a wrapper library would be a dependency to keep up
/// with for a handful of calls.
/// </summary>
/// <remarks>
/// <para>
/// Values are copied from the headers rather than from memory, and they are the ones that differ between systems
/// most quietly. <see cref="SpawnSetSid"/> is the example worth naming: it is <c>0x80</c> on glibc and
/// <c>0x0400</c> on Darwin, and the wrong one does not fail — the child starts outside the session that owns the
/// terminal, which shows up as a shell where Ctrl+C does nothing. (Darwin's <c>0x0002</c> is
/// <c>POSIX_SPAWN_SETPGROUP</c> — a process group, not a session; Apple's <c>bsd/sys/spawn.h</c> has both.)
/// </para>
/// <para>
/// The ioctl request numbers are the other kind: <see cref="TiocSetWindowSize"/> is <c>0x5414</c> on Linux and
/// <c>0x80087467</c> on Darwin. Passing the wrong one is not an error either — it is an unrecognised request, so
/// the window size is simply never set.
/// </para>
/// <para>
/// Two of the entry points are reached through Darwin's non-variadic stubs (<c>__open</c>, <c>__ioctl</c>) rather
/// than through their variadic wrappers. Apple's arm64 ABI passes variadic arguments on the stack, and a P/Invoke
/// signature has no way to say "variadic", so the marshaller puts the last argument in a register where the callee
/// will never look — measured on a real machine, the same pointer handed to a plain function arrives intact and
/// handed to a variadic one arrives as an unrelated stack address, which is how a window-size write corrupted
/// memory instead of failing. Darwin keeps a non-variadic stub for every one of these, because libc's own variadic
/// wrappers are compiled to call it with the argument in a register; the stubs cannot change while <c>ioctl</c>
/// still works, and they report failures through <c>errno</c> exactly as the wrappers do.
/// </para>
/// </remarks>
internal static unsafe partial class LibC
{
    /// <summary>
    /// Whether this is Darwin. Read once, because every constant below depends on it and none of them can change
    /// while the process lives.
    /// </summary>
    internal static readonly bool IsDarwin = OperatingSystem.IsMacOS();

    // linux/asm-generic/ioctls.h; Darwin's are _IOW('t', 103) / _IOR('t', 104) over struct winsize.
    internal static ulong TiocSetWindowSize => IsDarwin ? 0x8008_7467UL : 0x5414UL;
    internal static ulong TiocGetWindowSize => IsDarwin ? 0x4008_7468UL : 0x5413UL;

    // glibc's fcntl-linux.h: O_RDWR is 02, O_NOCTTY is 0400 — both octal. Darwin says 0x20000 for the latter.
    internal const int ORdWr = 0x0002;

    /// <summary>Do not make this the controlling terminal even if it is a tty; the child picks its own.</summary>
    internal static int ONoCtty => IsDarwin ? 0x20000 : 0x0100;

    // glibc's spawn.h: POSIX_SPAWN_SETSID is 0x0080. Darwin's spawn.h: 0x0400.
    internal static short SpawnSetSid => IsDarwin ? (short)0x0400 : (short)0x0080;

    internal const int SignalHangup = 1;
    internal const int SignalTerminate = 15;
    internal const int SignalKill = 9;

    /// <summary>Return immediately from <see cref="WaitPid"/> rather than blocking.</summary>
    internal const int WaitNoHang = 1;

    /// <summary>A system call that was interrupted by a signal; the caller should try it again.</summary>
    internal const int Interrupted = 4;

    /// <summary>There is something to read, or the terminal has been hung up.</summary>
    internal const short PollIn = 0x0001;

    // The opaque spawn structures are allocated by the caller and only ever written by the C library, so they are
    // declared as generous blocks rather than as a transcription of fields this code never reads. Getting a size
    // wrong would have the library write past the end of the buffer; being too large is free. glibc 2.39 needs 336
    // and 80 bytes for these; Darwin's are opaque pointers, so one pointer-sized slot holds what the library
    // allocates. See AllocateSpawnStorage for how one call shape serves both.
    internal const int SpawnAttributesSize = 512;
    internal const int SpawnFileActionsSize = 128;

    /// <summary>
    /// Storage for one opaque spawn object, in the form the running library expects: glibc fills the block in place,
    /// Darwin allocates the object itself and writes the pointer into the slot.
    /// </summary>
    /// <remarks>
    /// This is the one place the two libraries' designs are reconciled. Every spawn call takes the storage's address
    /// and the rest of the code is written once: glibc reads the struct at that address, Darwin reads the pointer
    /// stored there.
    /// </remarks>
    internal static void* AllocateSpawnStorage(int size) =>
        NativeMemory.AllocZeroed((nuint)(IsDarwin ? sizeof(nint) : size));

    /// <summary>
    /// The window size a terminal reports and accepts, in the layout the kernel expects.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixel;
        public ushort YPixel;
    }

    /// <summary>What <c>poll</c> watches and reports for one descriptor, as <c>struct pollfd</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFileDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    static LibC()
    {
        // On Darwin every binding here goes to libSystem, which is what "libc" means there — but the macOS port was
        // written and measured against the explicit path, so the name is resolved to it rather than trusted to the
        // runtime's probing. On Linux "libc" is left to the runtime, which maps it to glibc.
        if (IsDarwin)
        {
            NativeLibrary.SetDllImportResolver(typeof(LibC).Assembly, ResolveDarwin);
        }
    }

    private static nint ResolveDarwin(string libraryName, Assembly assembly, DllImportSearchPath? searchPaths) =>
        string.Equals(libraryName, "libc", StringComparison.Ordinal)
            ? NativeLibrary.Load("/usr/lib/libSystem.B.dylib", assembly, searchPaths)
            : 0;

    /// <summary>
    /// The effective user id, which is what the kernel checks when the process opens a file. <c>getuid</c> answers a
    /// different question and is the wrong one: a setuid program has both, and the effective one decides what it may
    /// reach.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint GetEffectiveUserId();

    /// <summary>
    /// Duplicates a file descriptor. Needed whenever a descriptor arrives over D-Bus, and when one descriptor has to
    /// serve as two streams whose lifetimes are independent.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    internal static partial int DuplicateFileDescriptor(int descriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int descriptor);

    [LibraryImport("libc", EntryPoint = "posix_openpt", SetLastError = true)]
    internal static partial int OpenPseudoTerminal(int flags);

    [LibraryImport("libc", EntryPoint = "grantpt", SetLastError = true)]
    internal static partial int GrantPseudoTerminal(int master);

    [LibraryImport("libc", EntryPoint = "unlockpt", SetLastError = true)]
    internal static partial int UnlockPseudoTerminal(int master);

    /// <summary>Darwin only: returns a <c>char*</c> owned by libc; do not free it.</summary>
    [LibraryImport("libc", EntryPoint = "ptsname", SetLastError = true)]
    private static partial byte* PseudoTerminalSlaveName(int master);

    /// <summary>The reentrant form, which glibc has and Darwin does not.</summary>
    [LibraryImport("libc", EntryPoint = "ptsname_r", SetLastError = true)]
    private static partial int PseudoTerminalSlaveNameInto(int master, byte* buffer, nuint size);

    /// <summary>
    /// The slave's name, written into the caller's buffer.
    /// </summary>
    /// <remarks>
    /// The reentrant form is used where it exists rather than <c>ptsname</c>, whose buffer is shared and would be a
    /// race in a process that starts more than one shell; on Darwin that form does not exist, and the copy is made
    /// here while the answer is still valid.
    /// </remarks>
    /// <returns>Zero on success, <c>-1</c> with <c>errno</c> set otherwise — the same contract as <c>ptsname_r</c>.</returns>
    internal static int GetSlaveName(int master, byte* buffer, nuint size)
    {
        if (!IsDarwin)
        {
            return PseudoTerminalSlaveNameInto(master, buffer, size);
        }

        var name = PseudoTerminalSlaveName(master);
        if (name is null)
        {
            return -1;
        }

        var length = 0;
        while (name[length] != 0)
        {
            length++;
        }

        if ((nuint)length >= size)
        {
            return -1;
        }

        new ReadOnlySpan<byte>(name, length).CopyTo(new Span<byte>(buffer, length));
        buffer[length] = 0;
        return 0;
    }

    // Darwin needs the non-variadic stub for open; see the class remarks.
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int OpenPlain(byte* path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "__open", SetLastError = true)]
    private static partial int OpenStub(byte* path, int flags, int mode);

    /// <param name="mode">Only read by the kernel when <c>O_CREAT</c> is set; ignored otherwise.</param>
    internal static int Open(byte* path, int flags) =>
        IsDarwin ? OpenStub(path, flags, 0) : OpenPlain(path, flags, 0);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoctlPlain(int descriptor, ulong request, WinSize* argument);

    [LibraryImport("libc", EntryPoint = "__ioctl", SetLastError = true)]
    private static partial int IoctlStub(int descriptor, ulong request, WinSize* argument);

    /// <summary>
    /// Sets or reads the terminal window size, and nothing else.
    /// </summary>
    /// <remarks>
    /// Declared as taking a <see cref="WinSize"/> pointer because that is the only request this host issues. On
    /// Darwin the entry point is the non-variadic stub rather than libc's variadic <c>ioctl</c>, for the reason
    /// given on the class; on glibc the plain declaration is safe, because the argument travels in a register on
    /// every architecture this runs on and arrives where the kernel expects it.
    /// </remarks>
    private static int Ioctl(int descriptor, ulong request, WinSize* argument) =>
        IsDarwin ? IoctlStub(descriptor, request, argument) : IoctlPlain(descriptor, request, argument);

    /// <summary>
    /// Applies a window size to a terminal descriptor.
    /// </summary>
    /// <remarks>
    /// Both ends of a pseudo-terminal accept this once a replica exists, so either descriptor will do. The size is
    /// clamped because <c>struct winsize</c> holds 16-bit fields and a resized window is an untrusted number.
    /// </remarks>
    internal static bool TrySetWindowSize(int descriptor, int columns, int rows)
    {
        var size = new WinSize
        {
            Columns = (ushort)Math.Clamp(columns, 1, ushort.MaxValue),
            Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
        };

        return Ioctl(descriptor, TiocSetWindowSize, &size) == 0;
    }

    /// <summary>Reads a window size back from a terminal descriptor.</summary>
    /// <remarks>Reading it back is the only way to tell that a write landed, which is what the tests rely on.</remarks>
    internal static bool TryGetWindowSize(int descriptor, out int columns, out int rows)
    {
        var size = default(WinSize);
        var result = Ioctl(descriptor, TiocGetWindowSize, &size);
        columns = size.Columns;
        rows = size.Rows;
        return result == 0;
    }

    [LibraryImport("libc", EntryPoint = "posix_spawn", SetLastError = true)]
    internal static partial int Spawn(
        int* pid,
        byte* path,
        void* fileActions,
        void* attributes,
        byte** argv,
        byte** envp);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_init", SetLastError = true)]
    internal static partial int SpawnFileActionsInit(void* fileActions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_destroy", SetLastError = true)]
    internal static partial int SpawnFileActionsDestroy(void* fileActions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2", SetLastError = true)]
    internal static partial int SpawnFileActionsAddDup2(void* fileActions, int descriptor, int target);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addclose", SetLastError = true)]
    internal static partial int SpawnFileActionsAddClose(void* fileActions, int descriptor);

    /// <summary>
    /// Opens a path in the child. Used for the terminal itself: inheriting an open descriptor does not make the
    /// terminal the child's controlling one — only opening it does — so the child opens the replica once on a spare
    /// descriptor and closes it again. The association belongs to the session, not to the descriptor.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addopen", SetLastError = true)]
    internal static partial int SpawnFileActionsAddOpen(void* fileActions, int descriptor, byte* path, int flags, int mode);

    /// <summary>Changes directory in the child, which <c>posix_spawn</c> has no other way to express.</summary>
    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np", SetLastError = true)]
    internal static partial int SpawnFileActionsAddChdir(void* fileActions, byte* directory);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_init", SetLastError = true)]
    internal static partial int SpawnAttributesInit(void* attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_destroy", SetLastError = true)]
    internal static partial int SpawnAttributesDestroy(void* attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setflags", SetLastError = true)]
    internal static partial int SpawnAttributesSetFlags(void* attributes, short flags);

    /// <summary>
    /// Waits for a child and returns its status, or -1. Blocks unless <see cref="WaitNoHang"/> is passed.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int WaitPid(int pid, int* status, int options);

    /// <summary>
    /// Signals a whole process group. The shell is made a session and process-group leader, so this is what reaches
    /// the shell and the jobs it kept in its own group — signalling the shell alone leaves background jobs running,
    /// because an ordinary shell does not pass a hangup on to them.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "killpg", SetLastError = true)]
    internal static partial int SignalProcessGroup(int group, int signal);

    /// <summary>Signals one process, used when a session has to be swept after its leader is gone.</summary>
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static partial int SignalProcess(int process, int signal);

    /// <summary>Waits for one of the descriptors to become ready, or for the timeout to run out.</summary>
    /// <param name="timeout">Milliseconds, or -1 to wait indefinitely.</param>
    /// <remarks>
    /// This is what makes a terminal read cancellable. A plain <c>read</c> on a terminal parks the thread until the
    /// shell speaks, and a <see cref="CancellationToken"/> cannot reach a thread that is inside the kernel: measured
    /// on a real machine, a read with a twenty-second timeout was still parked well after it had fired. Waiting in
    /// short slices and looking at the token in between is what makes the token mean something.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    internal static partial int Poll(PollFileDescriptor* descriptors, uint count, int timeout);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static partial nint Read(int descriptor, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    internal static partial nint Write(int descriptor, byte* buffer, nuint count);

    /// <summary>The exit code from a status word, or the signal that ended it.</summary>
    internal static int ExitCode(int status) =>
        (status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F);
}
