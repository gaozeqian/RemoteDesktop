using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.Posix.Interop;

/// <summary>
/// The C library entry points a Posix platform layer needs. Hand-written bindings, in the same style as the Windows
/// project's, rather than a package: the surface is small and a wrapper library would be a dependency to keep up
/// with for a handful of calls.
/// </summary>
/// <remarks>
/// <para>
/// Values are copied from glibc's headers rather than from memory, and they are the ones that differ between
/// systems most quietly. <see cref="SpawnSetSid"/> is the example worth naming: it is <c>0x80</c> on glibc and
/// <c>0x0400</c> on Darwin, and the wrong one does not fail — it starts the shell outside the session that owns the
/// terminal, which shows up as a shell where Ctrl+C does nothing.
/// </para>
/// <para>
/// The ioctl request numbers are the other kind: <see cref="TiocSetWindowSize"/> is <c>0x5414</c> on Linux and
/// <c>0x80087467</c> on Darwin. Passing the wrong one is not an error either — it is an unrecognised request, so
/// the window size is simply never set.
/// </para>
/// </remarks>
internal static unsafe partial class LibC
{
    // linux/asm-generic/ioctls.h
    internal const ulong TiocGetWindowSize = 0x5413;
    internal const ulong TiocSetWindowSize = 0x5414;

    // glibc's fcntl-linux.h: O_RDWR is 02 and O_NOCTTY is 0400, both octal.
    internal const int ORdWr = 0x0002;

    /// <summary>Do not make this the controlling terminal even if it is a tty; the child picks its own.</summary>
    internal const int ONoCtty = 0x0100;

    // glibc's spawn.h. Darwin says 0x0400 for the same name.
    internal const short SpawnSetSid = 0x0080;

    internal const int SignalHangup = 1;
    internal const int SignalTerminate = 15;
    internal const int SignalKill = 9;

    /// <summary>Return immediately from <see cref="WaitPid"/> rather than blocking.</summary>
    internal const int WaitNoHang = 1;

    /// <summary>A system call that was interrupted by a signal; the caller should try it again.</summary>
    internal const int Interrupted = 4;

    // The opaque spawn structures are allocated by the caller and only ever written by glibc, so they are declared
    // as generous blocks rather than as a transcription of fields this code never reads. Getting a size wrong would
    // have glibc write past the end of the buffer; being too large is free. glibc 2.39 needs 336 and 80 bytes.
    internal const int SpawnAttributesSize = 512;
    internal const int SpawnFileActionsSize = 128;
    internal const int SpawnAttributesAlignment = 16;
    internal const int SpawnFileActionsAlignment = 16;

    /// <summary>The window size a terminal reports and accepts, in the layout the kernel expects.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixel;
        public ushort YPixel;
    }

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

    /// <summary>
    /// The slave's name. The reentrant form is used rather than <c>ptsname</c>, whose buffer is shared and would be a
    /// race in a process that starts more than one shell.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "ptsname_r", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int GetSlaveName(int master, byte* buffer, nuint size);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(byte* path, int flags);

    // Declared non-variadic. glibc's ioctl takes the argument in a register on every architecture this runs on, so
    // the call arrives where the kernel expects it; the varargs dance the macOS layer needs is an Apple ABI quirk.
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int SetWindowSize(int descriptor, ulong request, WinSize* argument);

    [LibraryImport("libc", EntryPoint = "posix_spawn", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
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

    /// <summary>Changes directory in the child, which <c>posix_spawn</c> has no other way to express.</summary>
    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
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
    /// Signals a whole process group. Sending to the shell alone leaves what the shell started running: an ordinary
    /// shell does not pass a hangup on to its background jobs, so closing a terminal would leave them orphaned.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "killpg", SetLastError = true)]
    internal static partial int SignalProcessGroup(int group, int signal);

    /// <summary>The exit code from a status word, or the signal that ended it.</summary>
    internal static int ExitCode(int status) =>
        (status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F);
}
