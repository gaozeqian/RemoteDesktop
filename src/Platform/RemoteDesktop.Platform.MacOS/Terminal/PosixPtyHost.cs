using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Terminal;

/// <summary>
/// Runs shells on a BSD pseudo-terminal.
/// </summary>
/// <remarks>
/// <para>
/// The controller picks a name from <see cref="GetAvailableShells"/> and never supplies a path — the same rule the
/// Windows host follows, and the reason a path in <see cref="PtyStartOptions.Shell"/> is rejected rather than
/// executed.
/// </para>
/// <para>
/// The shell is started as a login shell (<c>-l</c>) so it picks up the user's real environment. Without it a remote
/// terminal would come up with the bare PATH a GUI process inherits, which on Apple Silicon means no Homebrew and a
/// visibly broken shell compared to the one the user gets in Terminal.app.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class PosixPtyHost : IPtyHost
{
    /// <summary>The names the controller sees, best default first, with the places each one lives.</summary>
    private static readonly (string Name, string[] Paths)[] Candidates =
    [
        ("zsh", ["/bin/zsh"]),
        ("bash", ["/opt/homebrew/bin/bash", "/usr/local/bin/bash", "/bin/bash"]),
        ("fish", ["/opt/homebrew/bin/fish", "/usr/local/bin/fish"]),
        ("sh", ["/bin/sh"]),
    ];

    public IReadOnlyList<string> GetAvailableShells() =>
        [.. Candidates.Where(candidate => Resolve(candidate.Name) is not null).Select(candidate => candidate.Name)];

    public IPtySession Start(PtyStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var available = GetAvailableShells();
        var name = string.IsNullOrEmpty(options.Shell) ? (available.Count > 0 ? available[0] : null) : options.Shell;
        var path = name is null ? null : Resolve(name);
        if (name is null || path is null)
        {
            throw new InvalidOperationException("No such shell is available.");
        }

        var directory = options.WorkingDirectory
            ?? Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // -l is the only argument added; everything the controller asked for follows it.
        string[] arguments = ["-l", .. options.Arguments];
        return PosixPtySession.Start(path, arguments, directory, options.Columns, options.Rows);
    }

    /// <summary>Resolves a shell name to a path, or <c>null</c> when that shell is not installed.</summary>
    internal static string? Resolve(string name)
    {
        foreach (var candidate in Candidates)
        {
            if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            return candidate.Paths.FirstOrDefault(File.Exists);
        }

        return null;
    }
}

/// <summary>
/// One shell on a pseudo-terminal, plus the two ends of its byte stream.
/// </summary>
/// <remarks>
/// <see cref="Output"/> and <see cref="Input"/> are separate objects over the same descriptor because the terminal
/// module reads and writes from different tasks at the same time. One <see cref="FileStream"/> used for both would
/// have to be shared across threads with a position that means nothing on a character device; a raw descriptor with
/// explicit read and write views has neither problem.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class PosixPtySession : IPtySession
{
    private readonly int _master;
    private readonly int _pid;
    private readonly Stream _output;
    private readonly Stream _input;
    private readonly Task<int> _exit;

    private PosixPtySession(int master, int pid)
    {
        _master = master;
        _pid = pid;
        _output = new PtyStream(master, write: false);
        _input = new PtyStream(master, write: true);
        _exit = Task.Factory.StartNew(
            () => WaitForExit(pid),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public Stream Output => _output;

    public Stream Input => _input;

    internal static unsafe PosixPtySession Start(string shellPath, string[] arguments, string directory, int columns, int rows)
    {
        var master = LibSystem.posix_openpt(LibSystem.ORdWr | LibSystem.ONoCtty);
        if (master < 0)
        {
            throw new IOException($"Could not open a pseudo-terminal (errno {Marshal.GetLastPInvokeError()}).");
        }

        var slave = -1;
        try
        {
            if (LibSystem.grantpt(master) != 0 || LibSystem.unlockpt(master) != 0)
            {
                throw new IOException($"Could not prepare the pseudo-terminal (errno {Marshal.GetLastPInvokeError()}).");
            }

            var slaveName = LibSystem.ToManagedString(LibSystem.ptsname(master))
                ?? throw new IOException("The pseudo-terminal has no replica name.");

            // The replica is opened here, in the parent, so that the window size can be applied before the shell
            // exists and without a second open racing the spawn. This very descriptor is then handed to the child,
            // which keeps a replica open at every instant and so keeps the size from being discarded.
            var slaveBytes = System.Text.Encoding.UTF8.GetBytes(slaveName + "\0");
            fixed (byte* name = slaveBytes)
            {
                slave = LibSystem.open(name, LibSystem.ORdWr | LibSystem.ONoCtty, 0);
            }

            if (slave < 0)
            {
                throw new IOException($"Could not open the pseudo-terminal replica '{slaveName}' (errno {Marshal.GetLastPInvokeError()}).");
            }

            _ = LibSystem.TrySetWindowSize(slave, columns, rows);

            // On Darwin both of these are opaque pointers, so the init call fills in a pointer rather than a struct.
            nint fileActions = 0;
            nint attributes = 0;
            var pid = 0;
            nint argv = 0;
            try
            {
                if (LibSystem.posix_spawn_file_actions_init(&fileActions) != 0 ||
                    LibSystem.posix_spawnattr_init(&attributes) != 0)
                {
                    throw new IOException("Could not initialise the spawn parameters.");
                }

                var directoryPath = System.Text.Encoding.UTF8.GetBytes(directory + "\0");
                if (LibSystem.posix_spawn_file_actions_adddup2(&fileActions, slave, 0) != 0 ||
                    LibSystem.posix_spawn_file_actions_adddup2(&fileActions, slave, 1) != 0 ||
                    LibSystem.posix_spawn_file_actions_adddup2(&fileActions, slave, 2) != 0 ||
                    (slave > 2 && LibSystem.posix_spawn_file_actions_addclose(&fileActions, slave) != 0))
                {
                    throw new IOException("Could not set up the shell's standard streams.");
                }

                // Inheriting a descriptor does not make the terminal the controlling one — only opening it does. So
                // the child opens the replica once on a spare descriptor, which claims the terminal, and closes it
                // again: the association belongs to the session, not to the descriptor.
                fixed (byte* name = slaveBytes)
                fixed (byte* working = directoryPath)
                {
                    if (LibSystem.posix_spawn_file_actions_addopen(&fileActions, 3, name, LibSystem.ORdWr, 0) != 0 ||
                        LibSystem.posix_spawn_file_actions_addclose(&fileActions, 3) != 0 ||
                        LibSystem.posix_spawn_file_actions_addchdir_np(&fileActions, working) != 0)
                    {
                        throw new IOException("Could not arrange the shell's controlling terminal.");
                    }
                }

                if (LibSystem.posix_spawnattr_setflags(&attributes, LibSystem.SpawnSetsid) != 0)
                {
                    throw new IOException("Could not make the shell a session leader.");
                }

                var pathBytes = System.Text.Encoding.UTF8.GetBytes(shellPath + "\0");
                argv = MarshalArguments(shellPath, arguments);
                fixed (byte* path = pathBytes)
                {
                    var status = LibSystem.posix_spawn(&pid, path, &fileActions, &attributes, argv, LibSystem.Environ);
                    if (status != 0)
                    {
                        throw new IOException($"Could not start '{shellPath}' (errno {status}).");
                    }
                }
            }
            finally
            {
                if (fileActions != 0)
                {
                    _ = LibSystem.posix_spawn_file_actions_destroy(&fileActions);
                }

                if (attributes != 0)
                {
                    _ = LibSystem.posix_spawnattr_destroy(&attributes);
                }

                FreeArguments(argv);
            }

            return new PosixPtySession(master, pid);
        }
        catch
        {
            _ = LibSystem.close(master);
            throw;
        }
        finally
        {
            if (slave >= 0)
            {
                // The child holds its own copy now, so this one is done. Closing it is also what lets the master see
                // end-of-file once the shell and everything it started have gone.
                _ = LibSystem.close(slave);
            }
        }
    }

    /// <summary>
    /// Sets the window size.
    /// </summary>
    /// <remarks>
    /// Applied to the master rather than by re-opening the replica: the master is held for the whole session and
    /// forwards the size to the terminal, and it is accepted for as long as any replica descriptor is open, which the
    /// shell's standard streams guarantee. Re-opening the replica would mean a path lookup and a descriptor per
    /// resize, for the same result.
    /// </remarks>
    public void Resize(int columns, int rows) => _ = LibSystem.TrySetWindowSize(_master, columns, rows);

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.WaitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        // The shell is a session and process-group leader, so signalling the group is what reaches the shell and
        // everything it started in the same group. Signalling the shell alone leaves background jobs running: an
        // ordinary shell does not pass the hangup on to them.
        _ = LibSystem.killpg(_pid, LibSystem.SignalHangup);
        _ = LibSystem.close(_master);
        _output.Dispose();
        _input.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The child's exit status is <c>wait</c>-style: the low byte is the signal when it was killed, and bits 8–15
    /// are the exit code otherwise.
    /// </summary>
    private static unsafe int WaitForExit(int pid)
    {
        var status = 0;
        if (LibSystem.waitpid(pid, &status, 0) < 0)
        {
            return -1;
        }

        var signal = status & 0x7f;
        return signal != 0 ? 128 + signal : (status >> 8) & 0xff;
    }

    /// <summary>
    /// Flattens the shell and its arguments into the C <c>argv</c> array <c>posix_spawn</c> expects. The block is
    /// freed by <see cref="FreeArguments"/> once the spawn has copied what it needs.
    /// </summary>
    private static unsafe nint MarshalArguments(string shellPath, string[] arguments)
    {
        var all = new string[arguments.Length + 1];
        all[0] = Path.GetFileName(shellPath);
        arguments.CopyTo(all, 1);

        var strings = new nint[all.Length + 1];
        for (var i = 0; i < all.Length; i++)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(all[i] + "\0");
            strings[i] = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, strings[i], bytes.Length);
        }

        strings[^1] = 0;
        var block = Marshal.AllocHGlobal(strings.Length * nint.Size);
        Marshal.Copy(strings, 0, block, strings.Length);
        return block;
    }

    private static unsafe void FreeArguments(nint block)
    {
        if (block == 0)
        {
            return;
        }

        var index = 0;
        while (true)
        {
            var item = ((nint*)block)[index++];
            if (item == 0)
            {
                break;
            }

            Marshal.FreeHGlobal(item);
        }

        Marshal.FreeHGlobal(block);
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
                var read = LibSystem.read(fd, p, (nuint)buffer.Length);
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

                var descriptor = new LibSystem.PollFileDescriptor { FileDescriptor = fd, Events = LibSystem.PollIn };
                var ready = LibSystem.poll(
                    &descriptor,
                    count: 1,
                    timeout: cancellationToken.CanBeCanceled ? PollIntervalMilliseconds : -1);

                if (ready > 0)
                {
                    return;
                }

                if (ready < 0 && Marshal.GetLastPInvokeError() != LibSystem.ErrnoInterrupted)
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
                    var count = LibSystem.write(fd, p + written, (nuint)(buffer.Length - written));
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
