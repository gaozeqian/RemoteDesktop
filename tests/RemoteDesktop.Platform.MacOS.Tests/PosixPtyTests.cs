using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.Posix.Terminal;

namespace RemoteDesktop.Platform.MacOS.Tests;

/// <summary>The real BSD pseudo-terminal with real shells.</summary>
public partial class PosixPtyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PosixPtyHost Host = new();

    private static readonly string[] OfferedShells = ["zsh", "bash", "fish", "sh"];

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
        await using var session = Host.Start(Options("sh"));

        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("echo hello-pty\nexit 7\n"), Ct);
        await session.Input.FlushAsync(Ct);
        var output = await ReadToEndAsync(session, TimeSpan.FromSeconds(20));

        Assert.Contains("hello-pty", output);
        Assert.Equal(7, await session.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task The_shell_gets_a_controlling_terminal_and_so_keeps_job_control()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");

        // -f keeps the user's rc files out of it: this test is about the terminal the shell was given, not about
        // whatever their prompt is configured to print.
        await using var session = Host.Start(Options("zsh", ["-f"]));

        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("echo tty=$(tty)\n"), Ct);
        await session.Input.FlushAsync(Ct);

        // Waiting for the command's output rather than for its echo: a terminal echoes what is typed, so matching
        // the command text would return before the shell had run anything. This also means the startup output has
        // been collected by the time the assertions run, which is where the complaint about job control appears.
        var output = await WaitForOutputAsync(session, "tty=/dev/tty", TimeSpan.FromSeconds(20));

        // The replica path is the proof the shell was attached to a pseudo-terminal at all...
        Assert.Contains("tty=/dev/tty", output);

        // ...and the absence of this complaint is the proof that it was made the controlling one. Without a
        // controlling terminal zsh starts without job control and says so.
        Assert.DoesNotContain("no job control", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_shell_sees_the_size_and_follows_resizes()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");
        await using var session = Host.Start(Options("sh", columns: 90, rows: 25));

        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("stty size\n"), Ct);
        await WaitForOutputAsync(session, "25 90", TimeSpan.FromSeconds(20));

        session.Resize(120, 40);
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("stty size\n"), Ct);
        await WaitForOutputAsync(session, "40 120", TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Disposing_ends_the_shell_and_what_it_started()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The BSD pseudo-terminal is macOS only.");
        var session = Host.Start(Options("sh"));

        // $! is the shell's own report of the background job's pid. Tracking that one process is what makes this
        // test mean what it says: counting every process named "sleep" on the machine made it fail whenever anything
        // else happened to be sleeping, which is exactly what a parallel test run produces.
        //
        // The awaited pattern needs digits after "child-", so the terminal's echo of the typed line — which contains
        // the literal "$!" — cannot satisfy it. Only the shell having run the line can.
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("sleep 300 &\necho child-$!\n"), Ct);
        await session.Input.FlushAsync(Ct);

        var output = await WaitForOutputAsync(session, text => ChildPattern().IsMatch(text), TimeSpan.FromSeconds(20));
        var childPid = int.Parse(ChildPattern().Match(output).Groups[1].Value, CultureInfo.InvariantCulture);

        Assert.True(IsRunning(childPid), $"The background sleep ({childPid}) should be running.");

        await session.DisposeAsync();

        // The whole process group gets SIGHUP, so the background child goes with the shell.
        var deadline = DateTime.UtcNow.AddSeconds(10);
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

    private static async Task<string> ReadToEndAsync(IPtySession session, TimeSpan timeout)
    {
        var text = new StringBuilder();
        var buffer = new byte[8192];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout);
        int read;
        while ((read = await session.Output.ReadAsync(buffer, limit.Token)) > 0)
        {
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    private static Task<string> WaitForOutputAsync(IPtySession session, string expected, TimeSpan timeout) =>
        WaitForOutputAsync(session, text => text.Contains(expected, StringComparison.Ordinal), timeout);

    private static async Task<string> WaitForOutputAsync(IPtySession session, Func<string, bool> isSatisfied, TimeSpan timeout)
    {
        var text = new StringBuilder();
        var buffer = new byte[8192];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout);
        while (!isSatisfied(text.ToString()))
        {
            var read = await session.Output.ReadAsync(buffer, limit.Token);
            Assert.True(read > 0, $"The output ended before the expected text appeared: {text}");
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return text.ToString();
    }
}
