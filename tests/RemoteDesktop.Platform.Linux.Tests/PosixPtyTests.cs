using System.Text;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.Posix.Terminal;

namespace RemoteDesktop.Platform.Linux.Tests;

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

        await WriteAsync(session, "echo marker-$((6 * 7))\n");

        Assert.Contains("marker-42", await output.UntilAsync("marker-42"));
    }

    [Fact]
    public async Task The_size_the_controller_asked_for_is_the_size_the_shell_sees()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        // Full-screen programs lay themselves out from this, so a terminal that reports the wrong shape draws to the
        // wrong shape and looks like a broken emulator.
        await using var session = Start("sh", columns: 100, rows: 40);
        var output = Read(session);

        await WriteAsync(session, "stty size\n");
        Assert.Contains("40 100", await output.UntilAsync("40 100"));
    }

    [Fact]
    public async Task Resizing_reaches_the_shell_without_it_being_asked()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        await using var session = Start("sh", columns: 80, rows: 24);
        var output = Read(session);

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

        // Start something long, then interrupt it the way a terminal does — by writing the control character, so that
        // the tty driver is what decides who gets signalled.
        await WriteAsync(session, "sleep 30\n");
        await Task.Delay(500, Ct);
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

        await WriteAsync(session, "exit 7\n");

        Assert.Equal(7, await session.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Closing_the_session_ends_the_shell_and_what_it_started()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        var session = Start("sh");
        var output = Read(session);

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
    public void A_shell_that_is_not_offered_is_refused()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), LinuxOnly);

        var host = new PosixPtyHost();

        Assert.Throws<InvalidOperationException>(() => host.Start(Options("definitely-not-a-shell")));

        // A path is not a name. Accepting one would turn the shell picker into a way to run anything at all.
        Assert.Throws<InvalidOperationException>(() => host.Start(Options("/bin/sh")));
    }

    private static PtyStartOptions Options(string shell) => new(shell, [], null, 80, 24);

    private static IPtySession Start(string shell, int columns = 80, int rows = 24) =>
        new PosixPtyHost().Start(new PtyStartOptions(shell, [], null, columns, rows));

    private static Task WriteAsync(IPtySession session, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return session.Input.WriteAsync(bytes, Ct).AsTask();
    }

    /// <summary>Reads the terminal continuously, so a test can wait for something to appear in it.</summary>
    private static OutputReader Read(IPtySession session) => new(session.Output);

    private sealed class OutputReader
    {
        private readonly StringBuilder _seen = new();
        private readonly Lock _gate = new();

        public OutputReader(Stream source)
        {
            _ = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (await source.ReadAsync(buffer, CancellationToken.None) is var read and > 0)
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
