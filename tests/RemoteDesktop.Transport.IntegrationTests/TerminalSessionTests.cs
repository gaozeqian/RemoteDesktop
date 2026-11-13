using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Features.Terminal.Emulation;
using RemoteDesktop.Platform.Windows.Terminal;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Terminal;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>Terminals over real sessions: the protocol and limits with a fake shell, and the whole path with real ConPTY shells.</summary>
public sealed class TerminalSessionTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_host_offers_its_shells_and_starts_the_one_asked_for()
    {
        await using var pair = await Pair.StartAsync(server, new FakePtyHost());

        Assert.Equal(["fake", "other"], await TerminalClient.ListShellsAsync(pair.Session.Context.Connection, Ct));
        await using var session = await TerminalSession.StartAsync(pair.Session.Context.Connection, "OTHER", 80, 24, Ct);

        Assert.Equal("other", session.Shell);
        await WaitForScreenAsync(session, "ready(other 80x24)");
    }

    [Fact]
    public async Task Typed_input_reaches_the_shell_and_its_output_lands_on_the_screen()
    {
        await using var pair = await Pair.StartAsync(server, new FakePtyHost());
        await using var session = await TerminalSession.StartAsync(pair.Session.Context.Connection, null, 80, 24, Ct);

        session.SendText("hello ");
        session.Paste("pasted\nlines");
        session.SendKey(TerminalKey.Up);
        session.SendControl('c', TerminalModifiers.Control);

        await WaitForScreenAsync(session, "hello pasted");
        await WaitForScreenAsync(session, "lines");
        await WaitForScreenAsync(session, "^C");
        var screen = session.Emulator.ScreenText();
        Assert.Contains("\\e[A", screen, StringComparison.Ordinal);
        Assert.Contains("^C", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resizes_reach_the_shell()
    {
        await using var pair = await Pair.StartAsync(server, new FakePtyHost());
        await using var session = await TerminalSession.StartAsync(pair.Session.Context.Connection, null, 80, 24, Ct);

        session.Resize(132, 43);

        await WaitForScreenAsync(session, "resized(132x43)");
        Assert.Equal((132, 43), (session.Emulator.Columns, session.Emulator.Rows));
    }

    [Fact]
    public async Task The_exit_code_is_reported_when_the_shell_ends()
    {
        await using var pair = await Pair.StartAsync(server, new FakePtyHost());
        await using var session = await TerminalSession.StartAsync(pair.Session.Context.Connection, null, 80, 24, Ct);
        var ended = new TaskCompletionSource();
        session.Ended += (_, _) => ended.TrySetResult();

        // The shell has to be up before it is told to leave: the fake writes this banner as soon as it exists, and a
        // line sent into a session that has not started yet is the race this waits out.
        await WaitForScreenAsync(session, "ready");

        session.SendText("exit\r");

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(5, session.ExitCode);
        Assert.Contains("goodbye", session.Emulator.ScreenText(), StringComparison.Ordinal); // output before the end is not lost
    }

    [Fact]
    public async Task Leaving_ends_the_shell_on_the_host()
    {
        var host = new FakePtyHost();
        await using var pair = await Pair.StartAsync(server, host);
        var session = await TerminalSession.StartAsync(pair.Session.Context.Connection, null, 80, 24, Ct);
        await WaitForScreenAsync(session, "ready");

        await session.DisposeAsync();

        await WaitUntilAsync(() => host.Started.All(s => s.Disposed));
    }

    [Fact]
    public async Task A_shell_that_is_not_offered_or_not_allowed_is_refused_with_a_reason()
    {
        var guard = new SwitchableGuard();
        await using var pair = await Pair.StartAsync(server, new FakePtyHost(), guard);
        var connection = pair.Session.Context.Connection;

        var unknown = await Assert.ThrowsAsync<TerminalException>(() => TerminalSession.StartAsync(connection, @"C:\Windows\System32\notepad.exe", 80, 24, Ct));
        Assert.Equal(TerminalError.ShellUnavailable, unknown.Error);

        guard.Reason = "Not on the login screen.";
        var refused = await Assert.ThrowsAsync<TerminalException>(() => TerminalSession.StartAsync(connection, null, 80, 24, Ct));
        Assert.Equal(TerminalError.NotAllowed, refused.Error);
        Assert.Contains("login screen", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<TerminalException>(() => TerminalClient.ListShellsAsync(connection, Ct));

        guard.Reason = null;
        await using var fine = await TerminalSession.StartAsync(connection, null, 80, 24, Ct);
    }

    [Fact]
    public async Task Only_a_limited_number_of_terminals_can_be_open_at_once()
    {
        await using var pair = await Pair.StartAsync(server, new FakePtyHost());
        var open = new List<TerminalSession>();
        try
        {
            for (var i = 0; i < TerminalModule.MaxTerminals; i++)
            {
                open.Add(await TerminalSession.StartAsync(pair.Session.Context.Connection, null, 80, 24, Ct));
            }

            var excess = await Assert.ThrowsAsync<TerminalException>(() => TerminalSession.StartAsync(pair.Session.Context.Connection, null, 80, 24, Ct));
            Assert.Equal(TerminalError.TooMany, excess.Error);

            await open[0].DisposeAsync();
            open.RemoveAt(0);
            await WaitUntilAsync(async () => await TryOpenAsync(pair, open));
        }
        finally
        {
            foreach (var session in open)
            {
                await session.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Watch_mode_grants_no_terminal()
    {
        await using var pair = await Pair.StartAsync(server, new FakePtyHost(), mode: SessionMode.View);
        Assert.False(pair.Session.Context.Grant.Allows(Permissions.Terminal));

        await Assert.ThrowsAsync<TerminalException>(() => TerminalSession.StartAsync(pair.Session.Context.Connection, null, 80, 24, Ct));
    }

    [Fact]
    public async Task A_real_windows_shell_runs_commands_and_reports_its_exit_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        await using var pair = await Pair.StartAsync(server, new ConPtyHost());
        await using var session = await TerminalSession.StartAsync(pair.Session.Context.Connection, "cmd", 100, 30, Ct);
        var ended = new TaskCompletionSource();
        session.Ended += (_, _) => ended.TrySetResult();

        await WaitForScreenAsync(session, ">");
        session.SendText("echo hello from cmd\r");
        await WaitForScreenAsync(session, "hello from cmd");
        session.Resize(90, 20);
        session.SendText("mode con\r");
        await WaitForScreenAsync(session, "90");
        session.SendText("exit 9\r");

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(9, session.ExitCode);
    }

    [Fact]
    public async Task Closing_the_connection_ends_a_real_shell_and_what_it_started()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        await using var pair = await Pair.StartAsync(server, new ConPtyHost());
        var session = await TerminalSession.StartAsync(pair.Session.Context.Connection, "cmd", 100, 30, Ct);
        var pingsBefore = Pings();
        await WaitForScreenAsync(session, ">");
        session.SendText("ping -n 120 127.0.0.1\r");

        // The shell's own ping is the one that was not there before. Counting them all would let an unrelated
        // ping.exe on the machine decide the result in either direction.
        await WaitUntilAsync(() => Pings().Except(pingsBefore).Any());
        var ours = Pings().Except(pingsBefore).ToArray();

        await pair.Session.DisposeAsync();

        await WaitUntilAsync(() => !ours.Any(IsRunning));
        await session.DisposeAsync();
    }

    private static HashSet<int> Pings()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("PING"))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // already gone, which is what the case expects
        }
    }

    private static async Task<bool> TryOpenAsync(Pair pair, List<TerminalSession> open)
    {
        try
        {
            open.Add(await TerminalSession.StartAsync(pair.Session.Context.Connection, null, 80, 24, Ct));
            return true;
        }
        catch (TerminalException)
        {
            return false;
        }
    }

    private static async Task WaitForScreenAsync(TerminalSession session, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!session.Emulator.ScreenText().Contains(expected, StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, $"'{expected}' never appeared on:\n{session.Emulator.ScreenText()}");
            await Task.Delay(25, Ct);
        }
    }

    private static Task WaitUntilAsync(Func<bool> condition) =>
        TestWait.UntilAsync(condition, "the terminal state", TimeSpan.FromSeconds(30));

    /// <summary>The same wait for a condition that has to be asked for over the connection.</summary>
    private static Task WaitUntilAsync(Func<Task<bool>> condition) =>
        TestWait.UntilAsync(condition, "the terminal state", TimeSpan.FromSeconds(30));

    private sealed class SwitchableGuard : IUserContextGuard
    {
        public volatile string? Reason;

        public string? GetRefusalReason() => Reason;
    }

    private sealed class Pair : IAsyncDisposable
    {
        private TestDevice _host = null!;
        private TestDevice _controller = null!;

        public RemoteSession Session { get; private set; } = null!;

        public static async Task<Pair> StartAsync(ServerFixture server, IPtyHost pty, IUserContextGuard? guard = null, SessionMode mode = SessionMode.Control)
        {
            var pair = new Pair();
            var module = new TerminalModule(pty, guard ?? new SwitchableGuard(), NullLogger<TerminalModule>.Instance);
            pair._host = await TestDevice.StartAsync(server, forceRelay: false, module);
            pair._controller = await TestDevice.StartAsync(server);
            pair.Session = await pair._controller.Controller.ConnectAsync(pair._host.Id, pair._host.Credentials.TemporaryPassword, mode, Ct);
            return pair;
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _controller.DisposeAsync();
            await _host.DisposeAsync();
        }
    }

    /// <summary>A shell that prints what it is told about itself and echoes what it receives.</summary>
    private sealed class FakePtyHost : IPtyHost
    {
        private readonly List<FakePtySession> _started = [];

        public IReadOnlyList<FakePtySession> Started
        {
            get
            {
                lock (_started)
                {
                    return [.. _started];
                }
            }
        }

        public IReadOnlyList<string> GetAvailableShells() => ["fake", "other"];

        public IPtySession Start(PtyStartOptions options)
        {
            var session = new FakePtySession(options.Shell ?? "fake", options.Columns, options.Rows);
            lock (_started)
            {
                _started.Add(session);
            }

            return session;
        }
    }

    private sealed class FakePtySession : IPtySession
    {
        private readonly AnonymousPipeServerStream _toShell = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _shellInput;
        private readonly AnonymousPipeClientStream _shellOutput;
        private readonly AnonymousPipeServerStream _fromShell = new(PipeDirection.In);
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private readonly Task _run;

        public FakePtySession(string shell, int columns, int rows)
        {
            _shellInput = new AnonymousPipeClientStream(PipeDirection.In, _toShell.ClientSafePipeHandle);
            _shellOutput = new AnonymousPipeClientStream(PipeDirection.Out, _fromShell.ClientSafePipeHandle);
            Write($"ready({shell} {columns}x{rows})\r\n");
            _run = Task.Run(RunAsync);
        }

        public Stream Output => _fromShell;

        public Stream Input => _toShell;

        public bool Disposed { get; private set; }

        public void Resize(int columns, int rows) => Write($"resized({columns}x{rows})\r\n");

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            _toShell.Dispose();
            _fromShell.Dispose();
            await _run.ConfigureAwait(false);
            _exit.TrySetResult(-1);
        }

        private void Write(string text)
        {
            lock (_lock)
            {
                try
                {
                    _shellOutput.Write(Encoding.UTF8.GetBytes(text));
                    _shellOutput.Flush();
                }
                catch (IOException)
                {
                }
            }
        }

        private async Task RunAsync()
        {
            var buffer = new byte[1024];
            var seen = new StringBuilder();
            try
            {
                int read;
                while ((read = await _shellInput.ReadAsync(buffer)) > 0)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, read);
                    seen.Append(text);
                    // Shows control characters the way a terminal would echo them.
                    Write(text.Replace("\u001b", "\\e", StringComparison.Ordinal).Replace("\u0003", "^C", StringComparison.Ordinal).Replace("\r", "\r\n", StringComparison.Ordinal));
                    if (seen.ToString().Contains("exit\r", StringComparison.Ordinal))
                    {
                        Write("goodbye\r\n");
                        _exit.TrySetResult(5);
                        break;
                    }
                }
            }
            catch (IOException)
            {
            }
            finally
            {
                _shellOutput.Dispose();
                _shellInput.Dispose();
            }
        }
    }
}
