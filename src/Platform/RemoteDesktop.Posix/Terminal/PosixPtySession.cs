using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
/// and anything the child runs before <c>exec</c> can deadlock on one of them. <c>posix_spawn</c> is a
/// <c>clone</c> with <c>CLONE_VM|CLONE_VFORK</c>, so there is no second address space to be in an inconsistent state.
/// </para>
/// <para>
/// That is why the file actions and attributes are passed as opaque buffers: they exist only for glibc to fill in
/// and for the kernel to act on, and this code never reads a field out of either.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
public sealed class PosixPtySession : IPtySession
{
    private const int SlaveNameCapacity = 256;

    private readonly int _master;
    private readonly int _processId;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int[] _size = new int[2];
    private bool _disposed;

    private PosixPtySession(int master, int processId, int columns, int rows)
    {
        _master = master;
        _processId = processId;
        _size[0] = columns;
        _size[1] = rows;

        // Two descriptors, one for each direction, so that disposing one stream does not take the other with it —
        // the terminal channel closes its input and output independently.
        _output = new FileStream(new SafeFileHandle(LibC.DuplicateFileDescriptor(master), ownsHandle: true), FileAccess.Read, bufferSize: 0);
        _input = new FileStream(new SafeFileHandle(LibC.DuplicateFileDescriptor(master), ownsHandle: true), FileAccess.Write, bufferSize: 0);

        _ = Task.Run(WaitForExit);
    }

    public Stream Output => _output;

    public Stream Input => _input;

    /// <summary>Starts a shell on a new pseudo-terminal.</summary>
    /// <exception cref="IOException">The terminal or the child could not be created.</exception>
    public static PosixPtySession Start(string shellPath, IReadOnlyList<string> arguments, string? directory, int columns, int rows)
    {
        ArgumentException.ThrowIfNullOrEmpty(shellPath);

        var master = LibC.OpenPseudoTerminal(LibC.ORdWr | LibC.ONoCtty);
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

            slave = OpenSlave(master);
            ApplyWindowSize(slave, columns, rows);

            var processId = Spawn(shellPath, arguments, directory, slave);
            return new PosixPtySession(master, processId, columns, rows);
        }
        catch
        {
            if (slave >= 0)
            {
                _ = LibC.Close(slave);
            }

            _ = LibC.Close(master);
            throw;
        }
    }

    /// <summary>
    /// Reports the terminal's size to the shell, which is what lets full-screen programs draw to the right shape.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Changing the reported size is enough: the kernel sends SIGWINCH to the foreground process group, so the
        // shell and anything it is running find out without being told.
        _size[0] = columns;
        _size[1] = rows;
        ApplyWindowSize(_master, columns, rows);
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

        // Hang up on the whole process group rather than the shell alone, so that whatever the shell started goes
        // with it instead of being left running with nothing attached to it.
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
    }

    private static unsafe int OpenSlave(int master)
    {
        var buffer = new byte[SlaveNameCapacity];
        fixed (byte* name = buffer)
        {
            if (LibC.GetSlaveName(master, name, SlaveNameCapacity) != 0)
            {
                throw new IOException($"Could not find the terminal's name (errno {Marshal.GetLastPInvokeError()}).");
            }

            // O_NOCTTY: the slave is opened without becoming this process's controlling terminal. It becomes the
            // shell's, in the child, which is where that belongs.
            var slave = LibC.Open(name, LibC.ORdWr | LibC.ONoCtty);
            if (slave < 0)
            {
                throw new IOException($"Could not open the terminal (errno {Marshal.GetLastPInvokeError()}).");
            }

            return slave;
        }
    }

    private static unsafe int Spawn(string shellPath, IReadOnlyList<string> arguments, string? directory, int slave)
    {
        var path = Encoding.UTF8.GetBytes(shellPath + "\0");
        var command = new string[arguments.Count + 1];
        command[0] = Path.GetFileName(shellPath);
        for (var i = 0; i < arguments.Count; i++)
        {
            command[i + 1] = arguments[i];
        }

        var actions = stackalloc byte[LibC.SpawnFileActionsSize];
        var attributes = stackalloc byte[LibC.SpawnAttributesSize];
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

    private static unsafe void ApplyWindowSize(int descriptor, int columns, int rows)
    {
        var size = new LibC.WinSize { Columns = (ushort)columns, Rows = (ushort)rows };

        // Best effort: a terminal whose size could not be reported is still perfectly usable, and failing here would
        // turn a cosmetic problem into a session that will not start.
        _ = LibC.SetWindowSize(descriptor, LibC.TiocSetWindowSize, &size);
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
}
