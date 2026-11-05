using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.Posix.Interop;

namespace RemoteDesktop.Platform.Posix.Terminal;

/// <summary>
/// One shell on a pseudo-terminal, plus the two ends of its byte stream.
/// </summary>
/// <remarks>
/// <para>
/// The child is started with <c>posix_spawn</c> rather than <c>fork</c>. A forked child of a .NET process shares its
/// address space with every other thread while the runtime's locks are in whatever state those threads left them,
/// and anything the child runs before <c>exec</c> can deadlock on one of them. <c>posix_spawn</c> is a clone with
/// <c>CLONE_VM|CLONE_VFORK</c> behind it, so there is no second address space to be in an inconsistent state — and
/// the file actions and attributes below are the structured way to say what should differ in the child.
/// </para>
/// <para>
/// The controlling terminal is arranged the way <c>forkpty</c> would have: the child is made a session leader, then
/// opens the replica without <c>O_NOCTTY</c>, so that <em>that open</em> is what claims the terminal. Inheriting an
/// open descriptor does not claim it — the association is made when a session leader opens a terminal — which is why
/// the replica is opened once more in the child even though its descriptor is already being handed over. Without
/// this the shell looks completely healthy and ignores Ctrl+C: the tty driver has nobody to deliver the interrupt
/// to.
/// </para>
/// <para>
/// Everything here is shared by the two Posix systems, and the differences are confined to <see cref="LibC"/>: the
/// constants (<c>TIOCSWINSZ</c>, <c>O_NOCTTY</c>, <c>POSIX_SPAWN_SETSID</c>) and the two entry points Darwin keeps
/// a non-variadic stub for. Those are exactly the differences that fail quietly, which is the argument for one
/// implementation rather than two.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class PosixPtySession : IPtySession
{
    private const int SlaveNameCapacity = 256;

    private readonly int _master;
    private readonly int _processId;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    private PosixPtySession(int master, int processId)
    {
        _master = master;
        _processId = processId;

        // Two objects over the same descriptor, one for each direction, because the terminal module reads and writes
        // from different tasks at the same time. Neither owns the descriptor — the session closes it once — so which
        // of them is disposed first does not matter.
        _output = new PtyStream(master, write: false);
        _input = new PtyStream(master, write: true);

        _ = Task.Factory.StartNew(
            WaitForExit,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public Stream Output => _output;

    public Stream Input => _input;

    /// <summary>Starts a shell on a new pseudo-terminal.</summary>
    /// <exception cref="IOException">The terminal or the child could not be created.</exception>
    public static PosixPtySession Start(string shellPath, IReadOnlyList<string> arguments, string? directory, int columns, int rows)
    {
        ArgumentException.ThrowIfNullOrEmpty(shellPath);

        // Close-on-exec: the shell must not inherit the master (its own or that of any other session), or the pty
        // stays allocated and a hangup never reaches the slave side while another process holds it open.
        var master = LibC.OpenPseudoTerminalCloseOnExec();
        if (master < 0)
        {
            throw new IOException($"Could not open a pseudo-terminal (errno {Marshal.GetLastPInvokeError()}).");
        }

        var slave = -1;
        try
        {
            // grantpt sets the slave's owner and permissions; unlockpt lets it be opened. Without either, opening the
            // slave fails or opens something the user does not own.
            if (LibC.GrantPseudoTerminal(master) != 0 || LibC.UnlockPseudoTerminal(master) != 0)
            {
                throw new IOException($"Could not prepare the pseudo-terminal (errno {Marshal.GetLastPInvokeError()}).");
            }

            var slaveName = GetSlaveName(master);

            // The replica is opened here, in the parent, so that the window size can be applied before the shell
            // exists and without a second open racing the spawn. This very descriptor is then handed to the child,
            // which keeps a replica open at every instant and so keeps the size from being discarded.
            unsafe
            {
                fixed (byte* name = slaveName)
                {
                    // O_NOCTTY: this open is not allowed to make the terminal this process's controlling one. It
                    // becomes the shell's, in the child, which is where that belongs.
                    // O_CLOEXEC keeps this descriptor out of any other program started meanwhile. The shell still gets
                    // the terminal: dup2 onto 0-2 yields descriptors without the flag, and the original is closed by
                    // a spawn file action.
                    slave = LibC.Open(name, LibC.ORdWr | LibC.ONoCtty | LibC.OCloExec);
                }
            }

            if (slave < 0)
            {
                throw new IOException($"Could not open the terminal (errno {Marshal.GetLastPInvokeError()}).");
            }

            // Best effort: a terminal whose size could not be reported is still perfectly usable, and failing here
            // would turn a cosmetic problem into a session that will not start.
            _ = LibC.TrySetWindowSize(slave, columns, rows);

            var processId = Spawn(shellPath, arguments, directory, slave, slaveName);
            return new PosixPtySession(master, processId);
        }
        catch
        {
            if (slave >= 0)
            {
                _ = LibC.Close(slave);
                slave = -1;
            }

            _ = LibC.Close(master);
            throw;
        }
        finally
        {
            if (slave >= 0)
            {
                // The child holds its own copy now, so this one is done. Closing it is also what lets the master see
                // end-of-file once the shell and everything it started have gone.
                _ = LibC.Close(slave);
            }
        }
    }

    /// <summary>
    /// Reports the terminal's size to the shell, which is what lets full-screen programs draw to the right shape.
    /// </summary>
    /// <remarks>
    /// Applied to the master rather than by re-opening the replica: the master is held for the whole session and
    /// forwards the size to the terminal, and it is accepted for as long as any replica descriptor is open, which the
    /// shell's standard streams guarantee. Changing the reported size is enough on its own — the kernel sends
    /// SIGWINCH to the foreground process group, so the shell and anything it is running find out without being told.
    /// </remarks>
    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _ = LibC.TrySetWindowSize(_master, columns, rows);
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
        _exit.Task.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);

        // The shell is a session and process-group leader, so signalling the group is what reaches the shell and
        // everything it started in the same group. Signalling the shell alone leaves background jobs running: an
        // ordinary shell does not pass the hangup on to them.
        _ = LibC.SignalProcessGroup(_processId, LibC.SignalHangup);
        _ = LibC.Close(_master);

        // Give the child a moment to go, then stop waiting: a shell that ignores the hangup must not hold up whoever
        // is closing this.
        try
        {
            await _exit.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = LibC.SignalProcessGroup(_processId, LibC.SignalKill);
        }

        // What the shell left behind in process groups of its own — a job-control shell puts every job in one — is
        // still the session's, and the group signals above cannot reach it. Where the session can be enumerated it is
        // swept now, so that "closing the terminal ends what it started" holds for shells that do not pass the hangup
        // on to their jobs (dash, for one, does not).
        SweepSession(_processId);
    }

    /// <summary>Sends a kill to every process still in the session, where the session can be enumerated.</summary>
    /// <remarks>
    /// Linux only. macOS has no <c>/proc</c>, and its shells do pass the hangup on to their jobs (both bash and zsh
    /// do), so the group signals above are the whole of what is available there.
    /// <para>
    /// The session id is the pid of the leader that was spawned, so a member is found by reading the session field of
    /// every <c>/proc</c> entry. A process whose own pid equals that session id is deliberately skipped: once the
    /// leader is gone, that can only be an unrelated process that has since been handed the recycled pid — its
    /// session would be itself — while every real member has a pid of its own.
    /// </para>
    /// </remarks>
    private static void SweepSession(int sessionId)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name, out var pid) || pid == sessionId || pid == Environment.ProcessId)
            {
                continue;
            }

            if (ReadSessionId(pid) == sessionId)
            {
                _ = LibC.SignalProcess(pid, LibC.SignalKill);
            }
        }
    }

    /// <summary>The session a process belongs to, from <c>/proc/&lt;pid&gt;/stat</c>, or <c>-1</c> when it cannot be read.</summary>
    private static int ReadSessionId(int pid)
    {
        string stat;
        try
        {
            stat = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The process is gone, or not ours to look at; either way it is not ours to signal.
            return -1;
        }

        // The second field is the command name in parentheses and may itself contain spaces and parentheses, so
        // everything before the *last* ')' is skipped. What follows is: state ppid pgrp session …
        var close = stat.LastIndexOf(')');
        if (close < 0)
        {
            return -1;
        }

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 4 && int.TryParse(fields[3], out var session) ? session : -1;
    }

    /// <summary>The slave's name as a null-terminated C string, from the library's own answer.</summary>
    private static byte[] GetSlaveName(int master)
    {
        var buffer = new byte[SlaveNameCapacity];
        var result = 0;
        unsafe
        {
            fixed (byte* name = buffer)
            {
                result = LibC.GetSlaveName(master, name, SlaveNameCapacity);
            }
        }

        if (result != 0)
        {
            throw new IOException($"Could not find the terminal's name (errno {Marshal.GetLastPInvokeError()}).");
        }

        var length = Array.IndexOf(buffer, (byte)0);
        return [.. buffer[..(length < 0 ? buffer.Length : length + 1)]];
    }

    private static unsafe int Spawn(string shellPath, IReadOnlyList<string> arguments, string? directory, int slave, byte[] slaveName)
    {
        var path = Encoding.UTF8.GetBytes(shellPath + "\0");
        var command = new string[arguments.Count + 1];
        command[0] = Path.GetFileName(shellPath);
        for (var i = 0; i < arguments.Count; i++)
        {
            command[i + 1] = arguments[i];
        }

        // The opaque spawn objects, in the form this system's C library expects — see LibC.AllocateSpawnStorage for
        // the one place the two designs are reconciled.
        var actions = LibC.AllocateSpawnStorage(LibC.SpawnFileActionsSize);
        var attributes = LibC.AllocateSpawnStorage(LibC.SpawnAttributesSize);
        var actionsInitialised = false;
        var attributesInitialised = false;

        byte** argv = null;
        byte** envp = null;
        byte* workingDirectory = null;
        try
        {
            if (LibC.SpawnFileActionsInit(actions) != 0 || LibC.SpawnAttributesInit(attributes) != 0)
            {
                throw new IOException($"Could not prepare to start '{shellPath}' (errno {Marshal.GetLastPInvokeError()}).");
            }

            actionsInitialised = true;
            attributesInitialised = true;

            // The slave becomes the child's standard streams, and the descriptor it came in on is closed again so the
            // child holds exactly one reference to its terminal.
            foreach (var target in (int[])[0, 1, 2])
            {
                if (LibC.SpawnFileActionsAddDup2(actions, slave, target) != 0)
                {
                    throw new IOException($"Could not arrange the shell's standard streams (errno {Marshal.GetLastPInvokeError()}).");
                }
            }

            if (slave > 2 && LibC.SpawnFileActionsAddClose(actions, slave) != 0)
            {
                throw new IOException($"Could not arrange the shell's standard streams (errno {Marshal.GetLastPInvokeError()}).");
            }

            // Inheriting a descriptor does not make the terminal the controlling one — only opening it does. So the
            // child opens the replica once on a spare descriptor, which claims the terminal, and closes it again: the
            // association belongs to the session, not to the descriptor.
            fixed (byte* name = slaveName)
            {
                if (LibC.SpawnFileActionsAddOpen(actions, 3, name, LibC.ORdWr, 0) != 0 ||
                    LibC.SpawnFileActionsAddClose(actions, 3) != 0)
                {
                    throw new IOException($"Could not arrange the shell's controlling terminal (errno {Marshal.GetLastPInvokeError()}).");
                }
            }

            if (!string.IsNullOrEmpty(directory))
            {
                workingDirectory = MarshalString(directory);
                if (LibC.SpawnFileActionsAddChdir(actions, workingDirectory) != 0)
                {
                    throw new IOException($"Could not start the shell in '{directory}' (errno {Marshal.GetLastPInvokeError()}).");
                }
            }

            // A new session, so the child has no controlling terminal until it takes the slave, and so that a
            // hangup sent to its process group cannot reach anything of ours.
            if (LibC.SpawnAttributesSetFlags(attributes, LibC.SpawnSetSid) != 0)
            {
                throw new IOException($"Could not make the shell a session leader (errno {Marshal.GetLastPInvokeError()}).");
            }

            argv = MarshalStrings(command);
            envp = MarshalStrings(EnvironmentForShell());

            int processId;
            fixed (byte* pathBytes = path)
            {
                if (LibC.Spawn(&processId, pathBytes, actions, attributes, argv, envp) != 0)
                {
                    throw new IOException($"Could not start '{shellPath}' (errno {Marshal.GetLastPInvokeError()}).");
                }
            }

            return processId;
        }
        finally
        {
            if (argv is not null)
            {
                FreeStrings(argv);
            }

            if (envp is not null)
            {
                FreeStrings(envp);
            }

            if (workingDirectory is not null)
            {
                Marshal.FreeHGlobal((nint)workingDirectory);
            }

            if (attributesInitialised)
            {
                _ = LibC.SpawnAttributesDestroy(attributes);
            }

            if (actionsInitialised)
            {
                _ = LibC.SpawnFileActionsDestroy(actions);
            }

            NativeMemory.Free(actions);
            NativeMemory.Free(attributes);
        }
    }

    /// <summary>
    /// The environment the shell starts with, which is this process's with <c>TERM</c> made sane.
    /// </summary>
    /// <remarks>
    /// Without a <c>TERM</c> the shell assumes something very old and turns off line editing, so the terminal the
    /// controller sees cannot correct a typed character or recall a command — and it looks like the emulator is at
    /// fault rather than the environment.
    /// </remarks>
    private static string[] EnvironmentForShell()
    {
        var variables = new List<string>();
        var hasTerm = false;

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name == "TERM")
            {
                hasTerm = true;
                variables.Add("TERM=" + (entry.Value?.ToString() is { Length: > 0 } value ? value : "xterm-256color"));
                continue;
            }

            variables.Add($"{name}={entry.Value}");
        }

        if (!hasTerm)
        {
            variables.Add("TERM=xterm-256color");
        }

        return [.. variables];
    }

    private unsafe void WaitForExit()
    {
        var status = 0;
        while (true)
        {
            var waited = LibC.WaitPid(_processId, &status, 0);
            if (waited == _processId)
            {
                break;
            }

            // A signal arriving while waiting is not a reason to stop waiting for the child.
            if (waited < 0 && Marshal.GetLastPInvokeError() != LibC.Interrupted)
            {
                status = 0;
                break;
            }
        }

        _exit.TrySetResult(LibC.ExitCode(status));
    }

    /// <summary>Flattens strings into the null-terminated array of null-terminated strings the C API expects.</summary>
    private static unsafe byte** MarshalStrings(string[] values)
    {
        var array = (byte**)Marshal.AllocHGlobal((values.Length + 1) * nint.Size);
        for (var i = 0; i < values.Length; i++)
        {
            array[i] = MarshalString(values[i]);
        }

        array[values.Length] = null;
        return array;
    }

    private static unsafe byte* MarshalString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var memory = (byte*)Marshal.AllocHGlobal(bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        memory[bytes.Length] = 0;
        return memory;
    }

    private static unsafe void FreeStrings(byte** array)
    {
        for (var i = 0; array[i] is not null; i++)
        {
            Marshal.FreeHGlobal((nint)array[i]);
        }

        Marshal.FreeHGlobal((nint)array);
    }

    /// <summary>One direction of a pseudo-terminal descriptor.</summary>
    private sealed unsafe class PtyStream(int fd, bool write) : Stream
    {
        /// <summary>
        /// How long a cancellable read waits before looking at the token again. A hundred milliseconds is short
        /// enough that tearing a session down feels immediate and long enough that an idle terminal wakes ten times
        /// a second, which costs nothing.
        /// </summary>
        private const int PollIntervalMilliseconds = 100;

        public override bool CanRead => !write;

        public override bool CanWrite => write;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);

        public override int Read(Span<byte> buffer) => ReadCore(buffer, CancellationToken.None);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            // Handing a blocking read to a pool thread would look asynchronous and not be: the caller's token could
            // not reach the thread parked inside the kernel, so a session being torn down would leave the read
            // outstanding for as long as the shell stayed quiet. Waiting on the descriptor in slices is what lets
            // the token be honoured, and costs nothing while output is flowing because the wait returns at once.
            return new ValueTask<int>(Task.Run(
                () => ReadCore(buffer.Span, cancellationToken),
                cancellationToken));
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteCore(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => WriteCore(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(cancellationToken);
            }

            return new ValueTask(Task.Run(
                () => WriteCore(buffer.Span),
                cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private int ReadCore(Span<byte> buffer, CancellationToken cancellationToken)
        {
            WaitForOutput(cancellationToken);

            fixed (byte* p = buffer)
            {
                var read = LibC.Read(fd, p, (nuint)buffer.Length);
                if (read < 0)
                {
                    throw new IOException($"Could not read from the terminal (errno {Marshal.GetLastPInvokeError()}).");
                }

                return (int)read;
            }
        }

        /// <summary>
        /// Blocks until the descriptor has something to say, or until the caller gives up.
        /// </summary>
        /// <remarks>
        /// A descriptor that has been hung up counts as ready: the read that follows returns zero, which is the
        /// end-of-file the callers are waiting for.
        /// </remarks>
        private void WaitForOutput(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var descriptor = new LibC.PollFileDescriptor { FileDescriptor = fd, Events = LibC.PollIn };
                var ready = LibC.Poll(
                    &descriptor,
                    count: 1,
                    timeout: cancellationToken.CanBeCanceled ? PollIntervalMilliseconds : -1);

                if (ready > 0)
                {
                    return;
                }

                if (ready < 0 && Marshal.GetLastPInvokeError() != LibC.Interrupted)
                {
                    throw new IOException($"Could not wait for the terminal (errno {Marshal.GetLastPInvokeError()}).");
                }
            }
        }

        private void WriteCore(ReadOnlySpan<byte> buffer)
        {
            fixed (byte* p = buffer)
            {
                var written = 0;
                while (written < buffer.Length)
                {
                    var count = LibC.Write(fd, p + written, (nuint)(buffer.Length - written));
                    if (count < 0)
                    {
                        throw new IOException($"Could not write to the terminal (errno {Marshal.GetLastPInvokeError()}).");
                    }

                    written += (int)count;
                }
            }
        }
    }
}
