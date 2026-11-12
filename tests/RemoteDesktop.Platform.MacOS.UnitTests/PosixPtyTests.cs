using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.Posix.Terminal;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

/// <summary>The real BSD pseudo-terminal with real shells.</summary>
public partial class PosixPtyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PosixPtyHost Host = new();

    private static readonly string[] OfferedShells = ["zsh", "bash", "fish", "sh"];

    /// <summary>
    /// Why the shell-interaction tests below do not run on this machine: on a hosted macOS runner the login shell resets
    /// the terminal more than once while it starts, and a line written into that window is echoed by the driver and then
    /// discarded — repeating it for thirty seconds does not get it executed. What the port does with the terminal is
    /// still covered here by the shell list, the refusal test and the ioctl round-trip in
    /// <c>PtyWindowSizeInteropTests</c>; the shell-side behaviour these four assert needs a Mac at a desk, and runs on
    /// Linux in <c>RemoteDesktop.Platform.Linux.UnitTests</c> against the same Posix implementation.
    /// </summary>
    private const string ShellDiscardsTypedLines = "Hosted macOS shells discard lines typed into them.";

    private static PtyStartOptions Options(string? shell, IReadOnlyList<string>? arguments = null, int columns = 100, int rows = 30) =>
        new(shell, arguments ?? [], null, columns, rows);

    [Fact]
    public void The_usual_shells_are_offered_and_nothing_else_is()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");
        var shells = Host.GetAvailableShells();

        Assert.NotEmpty(shells);
        Assert.Contains("zsh", shells);

        // Every offered name is one of the usual four, or the account's own login shell when it is something else
        // (the shared host puts $SHELL first) — never anything the controller could not ask for by name.
        var login = Path.GetFileName((Environment.GetEnvironmentVariable("SHELL") ?? string.Empty).TrimEnd('/'));
        Assert.All(shells, s => Assert.True(
            OfferedShells.Contains(s) || string.Equals(s, login, StringComparison.Ordinal),
            $"'{s}' is neither one of the usual shells nor the login shell."));
    }

    [Fact]
    public async Task Input_reaches_the_shell_and_its_output_comes_back_with_the_exit_code()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");
        Assert.SkipWhen(OperatingSystem.IsMacOS(), ShellDiscardsTypedLines);
        await using var session = Host.Start(Options("sh"));
        var output = new OutputReader(session.Output);

        // The empty quotes and the arithmetic keep the marker out of the echo of the typed line: only a shell that ran
        // the command prints "ty-42".
        var banner = await AskAsync(session, output, "echo hello-pl\"\"ty-$((6 * 7))\nexit 7\n", "ty-42", TimeSpan.FromSeconds(20));
        Assert.Contains("ty-42", banner);
        Assert.Equal(7, await session.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task The_shell_gets_a_controlling_terminal_and_so_keeps_job_control()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");
        Assert.SkipWhen(OperatingSystem.IsMacOS(), ShellDiscardsTypedLines);

        // -f keeps the user's rc files out of it: this test is about the terminal the shell was given, not about
        // whatever their prompt is configured to print.
        await using var session = Host.Start(Options("zsh", ["-f"]));
        var output = new OutputReader(session.Output);

        // Waiting for the command's output rather than for its echo: a terminal echoes what is typed, so matching
        // the command text would return before the shell had run anything. This also means the startup output has
        // been collected by the time the assertions run, which is where the complaint about job control appears.
        var banner = await AskAsync(session, output, "echo tty=$(tty)\n", "tty=/dev/tty", TimeSpan.FromSeconds(20));

        // The replica path is the proof the shell was attached to a pseudo-terminal at all...
        Assert.Contains("tty=/dev/tty", banner);

        // ...and the absence of this complaint is the proof that it was made the controlling one. Without a
        // controlling terminal zsh starts without job control and says so.
        Assert.DoesNotContain("no job control", banner, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_shell_sees_the_size_and_follows_resizes()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");
        Assert.SkipWhen(OperatingSystem.IsMacOS(), ShellDiscardsTypedLines);
        await using var session = Host.Start(Options("sh", columns: 90, rows: 25));
        var output = new OutputReader(session.Output);

        await AskAsync(session, output, "stty size\n", "25 90", TimeSpan.FromSeconds(20));

        session.Resize(120, 40);
        await AskAsync(session, output, "stty size\n", "40 120", TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Disposing_ends_the_shell_and_what_it_started()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");
        Assert.SkipWhen(OperatingSystem.IsMacOS(), ShellDiscardsTypedLines);
        var session = Host.Start(Options("sh"));
        var output = new OutputReader(session.Output);

        // $! is the shell's own report of the background job's pid. Tracking that one process is what makes this
        // test mean what it says: counting every process named "sleep" on the machine made it fail whenever anything
        // else happened to be sleeping, which is exactly what a parallel test run produces.
        //
        // The awaited pattern needs digits after "child-", so the terminal's echo of the typed line — which contains
        // the literal "$!" — cannot satisfy it. Only the shell having run the line can.
        var banner = await AskAsync(
            session,
            output,
            "sleep 300 &\necho child-$!\n",
            text => ChildPattern().IsMatch(text),
            TimeSpan.FromSeconds(20));
        var childPid = int.Parse(ChildPattern().Match(banner).Groups[1].Value, CultureInfo.InvariantCulture);

        Assert.True(IsRunning(childPid), $"The background sleep ({childPid}) should be running.");

        await session.DisposeAsync();

        // The whole process group gets SIGHUP, so the background child goes with the shell. The deadline is a load
        // tolerance rather than a timing claim: this runs beside the whole integration suite, and a contended runner
        // can take a while to let a signal work its way through a shell.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (IsRunning(childPid))
        {
            Assert.True(DateTime.UtcNow < deadline, $"The background process {childPid} outlived its shell.");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public void A_shell_that_is_not_offered_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");

        // A path is not a name: the controller picks from the list, never supplies a program to run.
        Assert.Throws<InvalidOperationException>(() => Host.Start(Options("/usr/bin/yes")));
        Assert.Throws<InvalidOperationException>(() => Host.Start(Options("nc")));
    }

    /// <summary>The shell's report of the background job's pid; the digits rule out matching its own echo.</summary>
    [GeneratedRegex(@"child-(\d+)")]
    private static partial Regex ChildPattern();

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Types a command and waits for what the shell prints for it, typing it again until that appears. A login shell
    /// prints nothing while it runs the system profile — seconds on a hosted runner — and resets the terminal
    /// afterwards; a line that arrives in that window is echoed by the driver and then discarded, which is what the
    /// transcript's two prompts with nothing between them were. One write is not enough there; a repeated one is.
    /// </summary>
    private static async Task<string> AskAsync(
        IPtySession session, OutputReader output, string command, Func<string, bool> isAnswered, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await session.Input.WriteAsync(Encoding.UTF8.GetBytes(command), Ct);
            await session.Input.FlushAsync(Ct);
            try
            {
                // Short, because the answer to a line that was swallowed is nothing at all: waiting the whole budget
                // on the first attempt only delays the retry that lands.
                return await output.UntilAsync(isAnswered, TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
                Assert.True(DateTime.UtcNow < deadline, "The shell never answered.");
            }
        }
    }

    private static Task<string> AskAsync(
        IPtySession session, OutputReader output, string command, string marker, TimeSpan timeout) =>
        AskAsync(session, output, command, text => text.Contains(marker, StringComparison.Ordinal), timeout);

    /// <summary>Reads the terminal continuously, so a test can wait for something to appear in it.</summary>
    private sealed class OutputReader
    {
        private readonly StringBuilder _seen = new();
        private readonly Lock _gate = new();

        public OutputReader(Stream source)
        {
            var reading = Ct;
            _ = Task.Run(async () =>
            {
                var buffer = new byte[8192];
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

        public Task<string> UntilAsync(string marker, TimeSpan timeout) =>
            UntilAsync(text => text.Contains(marker, StringComparison.Ordinal), timeout);

        /// <summary>Waits for <paramref name="isSatisfied"/> and returns everything read so far.</summary>
        public async Task<string> UntilAsync(Func<string, bool> isSatisfied, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    var text = _seen.ToString();
                    if (isSatisfied(text))
                    {
                        return text;
                    }
                }

                await Task.Delay(25, Ct);
            }

            lock (_gate)
            {
                throw new TimeoutException($"The expected text never appeared. The terminal had:\n{_seen}");
            }
        }
    }
}
