using System.Text;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.Posix.Interop;
using RemoteDesktop.Platform.Posix.Terminal;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// A real shell on a real pseudo-terminal.
/// </summary>
/// <remarks>
/// These run actual programs, so they are the slowest tests here and the only ones that can catch the mistakes a
/// pseudo-terminal layer actually makes. Starting the child outside its own session, for instance, produces a shell
/// that looks completely healthy and ignores Ctrl+C — nothing that reads a result would notice.
/// </remarks>
public sealed class PosixPtyTests
{
    private const string LinuxOnly = "A Posix pseudo-terminal.";

    // Seen on the macOS runner: after the shell has answered one command it resets the terminal again, and the line
    // typed next is echoed by the driver and then thrown away — the transcript shows two prompts with nothing between
    // them. What the size tests assert is therefore not observable through a shell there; the ioctl they depend on is
    // covered on both systems by The_terminal_reports_the_size_the_platform_layer_set, which needs no shell.
    private const string ShellSwallowsLinesTypedIntoIt = "This shell discards lines typed into it.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_shells_offered_are_the_ones_this_machine_has()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        var shells = new PosixPtyHost().GetAvailableShells();

        Assert.NotEmpty(shells);
        // Every machine that can run this has a Bourne shell somewhere; if even that is missing the list is wrong.
        Assert.Contains(shells, name => name is "sh" or "bash" or "dash");

        // Everything offered must be a name a controller could ask for, not a path.
        Assert.All(shells, name => Assert.DoesNotContain('/', name));
    }

    [Fact]
    public async Task A_shell_starts_and_runs_a_command()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        await using var session = Start("sh");
        var output = Read(session);
        await WaitForShellAsync(session, output);

        await WriteAsync(session, "echo marker-$((6 * 7))\n");

        Assert.Contains("marker-42", await output.UntilAsync("marker-42"));
    }

    [Fact]
    public void The_terminal_reports_the_size_the_platform_layer_set()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        // Full-screen programs lay themselves out from this, so a terminal that reports the wrong shape draws to the
        // wrong shape and looks like a broken emulator. Read back from the replica, which is the descriptor a shell
        // asks: the same ioctl, so a wrong request number on either system is what this catches.
        var master = LibC.OpenPseudoTerminalCloseOnExec();
        Assert.True(master >= 0);
        var slave = -1;
        try
        {
            Assert.Equal(0, LibC.GrantPseudoTerminal(master));
            Assert.Equal(0, LibC.UnlockPseudoTerminal(master));
            slave = OpenReplica(master);
            Assert.True(slave >= 0);

            Assert.True(LibC.TrySetWindowSize(master, columns: 100, rows: 40));

            Assert.True(LibC.TryGetWindowSize(slave, out var columns, out var rows));
            Assert.Equal((100, 40), (columns, rows));
        }
        finally
        {
            if (slave >= 0)
            {
                _ = LibC.Close(slave);
            }

            _ = LibC.Close(master);
        }
    }

    [Fact]
    public async Task The_size_the_controller_asked_for_is_the_size_the_shell_sees()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);
        Assert.SkipWhen(OperatingSystem.IsMacOS(), ShellSwallowsLinesTypedIntoIt);

        // The end-to-end half of the test above: the shell itself, asked through stty, agrees.
        await using var session = Start("sh", columns: 100, rows: 40);
        var output = Read(session);
        await WaitForShellAsync(session, output);

        await WriteAsync(session, "stty size\n");
        Assert.Contains("40 100", await output.UntilAsync("40 100"));
    }

    [Fact]
    public async Task Resizing_reaches_the_shell_without_it_being_asked()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);
        Assert.SkipWhen(OperatingSystem.IsMacOS(), ShellSwallowsLinesTypedIntoIt);

        await using var session = Start("sh", columns: 80, rows: 24);
        var output = Read(session);
        await WaitForShellAsync(session, output);

        await WriteAsync(session, "stty size\n");
        await output.UntilAsync("24 80");

        session.Resize(120, 50);

        await WriteAsync(session, "stty size\n");
        Assert.Contains("50 120", await output.UntilAsync("50 120"));
    }

    [Fact]
    public async Task Ctrl_c_stops_what_the_shell_is_running()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        await using var session = Start("sh");
        var output = Read(session);
        await WaitForShellAsync(session, output);

        // Start something long that announces itself first, then interrupt it the way a terminal does — by writing the
        // control character, so the tty driver is what decides who gets signalled. The announcement is what makes the
        // interrupt land on the sleep: a fixed pause only guesses at how long the shell takes to start it, and on a
        // contended runner the ^C then reaches the prompt instead.
        await WriteAsync(session, "echo running-$((6 * 7)); sleep 30; echo slept-42\n");
        await output.UntilAsync("running-42", TimeSpan.FromSeconds(10));
        await WriteAsync(session, "\u0003");

        // If the shell were not the leader of its own session with this terminal as its controlling one, the tty
        // would have nobody to send the interrupt to and the sleep would run to the end — so this line would not be
        // reached for thirty seconds.
        await WriteAsync(session, "echo interrupted\n");
        Assert.Contains("interrupted", await output.UntilAsync("interrupted", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task The_exit_code_of_what_ran_comes_back()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        await using var session = Start("sh");
        var output = Read(session);
        await WaitForShellAsync(session, output);

        await WriteAsync(session, "exit 7\n");

        Assert.Equal(7, await session.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Closing_the_session_ends_the_shell_and_what_it_started()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        var session = Start("sh");
        var output = Read(session);
        await WaitForShellAsync(session, output);

        // Something that would outlive the shell: a plain shell does not pass a hangup on to its background jobs, so
        // signalling only the shell would leave this running with nothing attached to it.
        //
        // The marker is typed with an empty quote inside it — star""ted — so that the shell prints "started" while the
        // line the terminal echoes back contains something else. A terminal echoes what is written to it, so without
        // that the marker would appear in the output before the command had even run.
        await WriteAsync(session, "sleep 30 &\necho star\"\"ted-$!\n");
        var started = await output.UntilAsync("started-");
        var backgroundId = int.Parse(started.Split("started-")[1].Split(['\r', '\n'])[0], System.Globalization.CultureInfo.InvariantCulture);

        await session.DisposeAsync();

        // The process is gone, so signalling it now fails.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && Directory.Exists($"/proc/{backgroundId}"))
        {
            await Task.Delay(50, Ct);
        }

        Assert.False(Directory.Exists($"/proc/{backgroundId}"), $"Process {backgroundId} outlived the session.");
    }

    [Fact]
    public async Task The_shell_does_not_inherit_the_masters_of_pseudo_terminals()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // A master that is not close-on-exec leaks into every child of the host, so the shell would hold the master of
        // its own terminal (and of every other session's) open: the pty slot stays allocated and a daemon on the slave
        // side does not see a hangup when the host closes its end.
        await using var session = Start("sh");

        var descriptors = await ListOpenDescriptorsAsync(session);

        Assert.DoesNotContain("ptmx", descriptors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_sessions_shell_cannot_see_another_sessions_master()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        await using var first = Start("sh");
        await using var second = Start("sh");

        // Both are alive at once, which is when a leaked master matters: the second shell is spawned while the first
        // session's master is open in the host.
        var seenBySecond = await ListOpenDescriptorsAsync(second);
        var seenByFirst = await ListOpenDescriptorsAsync(first);

        Assert.DoesNotContain("ptmx", seenBySecond, StringComparison.Ordinal);
        Assert.DoesNotContain("ptmx", seenByFirst, StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void Close_on_exec_can_be_set_on_a_descriptor_that_was_opened_without_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // This is the path macOS takes for the master, because posix_openpt there only promises to accept O_RDWR and
        // O_NOCTTY. Here it is checked against the Linux request number, which is the part that can be measured.
        var master = LibC.OpenPseudoTerminal(LibC.ORdWr | LibC.ONoCtty);
        Assert.True(master >= 0);
        try
        {
            Assert.False(HasCloseOnExec(master));

            Assert.True(LibC.TrySetCloseOnExec(master));

            Assert.True(HasCloseOnExec(master));
        }
        finally
        {
            _ = LibC.Close(master);
        }
    }

    [Fact]
    public void A_pseudo_terminal_is_opened_close_on_exec()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var master = LibC.OpenPseudoTerminalCloseOnExec();
        Assert.True(master >= 0);
        try
        {
            Assert.True(HasCloseOnExec(master));
        }
        finally
        {
            _ = LibC.Close(master);
        }
    }

    [Fact]
    public void A_shell_that_is_not_offered_is_refused()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        var host = new PosixPtyHost();

        Assert.Throws<InvalidOperationException>(() => host.Start(Options("definitely-not-a-shell")));

        // A path is not a name. Accepting one would turn the shell picker into a way to run anything at all.
        Assert.Throws<InvalidOperationException>(() => host.Start(Options("/bin/sh")));
    }

    /// <summary>
    /// The replica side of a pseudo-terminal, opened the way the session opens it: the terminal's other end, which is
    /// the descriptor a program on it would ask for its size.
    /// </summary>
    private static unsafe int OpenReplica(int master)
    {
        var buffer = new byte[256];
        fixed (byte* name = buffer)
        {
            Assert.Equal(0, LibC.GetSlaveName(master, name, (nuint)buffer.Length));
        }

        var length = Array.IndexOf(buffer, (byte)0);
        buffer[length < 0 ? buffer.Length - 1 : length] = 0;
        fixed (byte* name = buffer)
        {
            return LibC.Open(name, LibC.ORdWr | LibC.ONoCtty);
        }
    }

    /// <summary>
    /// What the shell has open, as <c>ls -l</c> prints it: the shell runs <c>ls</c>, so these are the descriptors a
    /// child of the shell inherits, which is the question.
    /// </summary>
    private static async Task<string> ListOpenDescriptorsAsync(IPtySession session)
    {
        var output = Read(session);
        await WaitForShellAsync(session, output);

        // The marker is typed as don""e so that the line the terminal echoes back does not contain it.
        await WriteAsync(session, "ls -l /proc/self/fd; echo don\"\"e-$((6 * 7))\n");
        var text = await output.UntilAsync("done-42");

        // The listing must really be there, or "no ptmx" would be true of an empty answer.
        Assert.Contains("/dev/pts/", text, StringComparison.Ordinal);
        return text;
    }

    private static bool HasCloseOnExec(int descriptor)
    {
        // The flags line of fdinfo is octal, and O_CLOEXEC is 02000000.
        var flags = File.ReadAllLines($"/proc/self/fdinfo/{descriptor}")
            .Single(line => line.StartsWith("flags:", StringComparison.Ordinal))
            .Split(':')[1].Trim();
        return (Convert.ToInt32(flags, 8) & 0x80000) != 0;
    }

    private static PtyStartOptions Options(string shell) => new(shell, [], null, 80, 24);

    private static IPtySession Start(string shell, int columns = 80, int rows = 24) =>
        new PosixPtyHost().Start(new PtyStartOptions(shell, [], null, columns, rows));

    private static Task WriteAsync(IPtySession session, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return session.Input.WriteAsync(bytes, Ct).AsTask();
    }

    /// <summary>
    /// Waits until the shell is reading, by typing a probe until one comes back answered. A login shell prints nothing
    /// while it runs the system profile, and a line typed into that window is echoed by the terminal driver and then
    /// discarded — macOS's bash, with a profile that takes seconds on a hosted runner, is where this bit; Linux's dash
    /// is ready before the first line arrives. Every test here types into a shell that has just started, so each one
    /// waits for this first rather than assuming the shell was already listening.
    /// </summary>
    private static async Task WaitForShellAsync(IPtySession session, OutputReader output)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await WriteAsync(session, "echo ready-$((6 * 7))\n");
            try
            {
                // The probe is repeated because one written before the shell was reading is lost, and the marker is
                // only printed by a shell that ran the command: the echoed line carries the $((6 * 7)) text instead.
                await output.UntilAsync("ready-42", TimeSpan.FromMilliseconds(500));
                return;
            }
            catch (TimeoutException)
            {
                Assert.True(DateTime.UtcNow < deadline, "The shell never answered.");
            }
        }
    }

    /// <summary>Reads the terminal continuously, so a test can wait for something to appear in it.</summary>
    private static OutputReader Read(IPtySession session) => new(session.Output);

    private sealed class OutputReader
    {
        private readonly StringBuilder _seen = new();
        private readonly Lock _gate = new();

        public OutputReader(Stream source)
        {
            // Captured here rather than read inside the loop: the background task must stop with the test even if
            // the test never gets as far as disposing the session.
            var reading = Ct;
            _ = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (await source.ReadAsync(buffer, reading) is var read and > 0)
                    {
                        lock (_gate)
                        {
                            _seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // The session was closed, which is how these tests end.
                }
            });
        }

        /// <summary>Waits for <paramref name="marker"/> and returns everything read up to and including it.</summary>
        public async Task<string> UntilAsync(string marker, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    var text = _seen.ToString();
                    if (text.Contains(marker, StringComparison.Ordinal))
                    {
                        return text;
                    }
                }

                await Task.Delay(25, Ct);
            }

            lock (_gate)
            {
                throw new TimeoutException($"'{marker}' never appeared. The terminal had:\n{_seen}");
            }
        }
    }
}
