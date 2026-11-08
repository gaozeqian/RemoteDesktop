using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Features.Terminal.Emulation;

namespace RemoteDesktop.E2E.IntegrationTests;

/// <summary>
/// End-to-end integration tests for remote pseudo-terminal (PTY) sessions over live cluster connections,
/// verifying shell enumeration, interactive I/O stream encoding/decoding, VT emulation,
/// dynamic terminal resize, session termination, and watch-mode rejection.
/// </summary>
public sealed class TerminalE2ETests(EndToEndClusterFixture cluster) : IClassFixture<EndToEndClusterFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Host_lists_shells_and_starts_requested_shell_with_dimensions()
    {
        var ptyHost = new TestPtyHost();
        var module = new TerminalModule(ptyHost, new PermissiveUserContextGuard(), NullLogger<TerminalModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        var shells = await TerminalClient.ListShellsAsync(session.Context.Connection, Ct);
        Assert.Equal(["bash", "sh", "custom"], shells);

        await using var terminal = await TerminalSession.StartAsync(
            session.Context.Connection,
            "custom",
            100,
            30,
            Ct);

        Assert.Equal("custom", terminal.Shell);
        await WaitForScreenAsync(terminal, "ready(custom 100x30)");
    }

    [Fact]
    public async Task Typed_input_keys_and_control_sequences_reach_shell_and_render_on_emulator()
    {
        var ptyHost = new TestPtyHost();
        var module = new TerminalModule(ptyHost, new PermissiveUserContextGuard(), NullLogger<TerminalModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        await using var terminal = await TerminalSession.StartAsync(
            session.Context.Connection,
            "bash",
            80,
            24,
            Ct);

        await WaitForScreenAsync(terminal, "ready(bash 80x24)");

        terminal.SendText("echo 'e2e-terminal'\r");
        terminal.Paste("pasted-input\n");
        terminal.SendKey(TerminalKey.Up);
        terminal.SendControl('c', TerminalModifiers.Control);

        await WaitForScreenAsync(terminal, "e2e-terminal");
        await WaitForScreenAsync(terminal, "pasted-input");
        await WaitForScreenAsync(terminal, "^C");

        var screen = terminal.Emulator.ScreenText();
        Assert.Contains("e2e-terminal", screen, StringComparison.Ordinal);
        Assert.Contains("\\e[A", screen, StringComparison.Ordinal); // Up arrow sequence
        Assert.Contains("^C", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_window_resizes_propagate_to_remote_pty_session()
    {
        var ptyHost = new TestPtyHost();
        var module = new TerminalModule(ptyHost, new PermissiveUserContextGuard(), NullLogger<TerminalModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        await using var terminal = await TerminalSession.StartAsync(
            session.Context.Connection,
            null,
            80,
            24,
            Ct);

        await WaitForScreenAsync(terminal, "ready");

        terminal.Resize(120, 40);
        await WaitForScreenAsync(terminal, "resized(120x40)");
    }

    [Fact]
    public async Task Closing_terminal_session_terminates_pty_and_cleans_up_resources()
    {
        var ptyHost = new TestPtyHost();
        var module = new TerminalModule(ptyHost, new PermissiveUserContextGuard(), NullLogger<TerminalModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.Control,
            Ct);

        var terminal = await TerminalSession.StartAsync(
            session.Context.Connection,
            null,
            80,
            24,
            Ct);

        Assert.Single(ptyHost.StartedSessions);
        var ptySession = ptyHost.StartedSessions[0];

        await terminal.DisposeAsync();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ptySession.Disposed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(ptySession.Disposed);
    }

    [Fact]
    public async Task Watch_mode_session_rejects_terminal_channel()
    {
        var ptyHost = new TestPtyHost();
        var module = new TerminalModule(ptyHost, new PermissiveUserContextGuard(), NullLogger<TerminalModule>.Instance);

        await using var host = await cluster.StartNodeAsync(new EndToEndNodeOptions { Features = [module] }, Ct);
        await using var controller = await cluster.StartNodeAsync(cancellationToken: Ct);

        // Connect in View-only mode
        await using var session = await controller.ConnectAsync(
            host.Id,
            host.Credentials.TemporaryPassword,
            SessionMode.View,
            Ct);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            TerminalSession.StartAsync(session.Context.Connection, null, 80, 24, Ct));
        Assert.True(
            error is TerminalException or System.Net.Quic.QuicException,
            $"Expected the watch-mode channel refusal, got {error.GetType().FullName}: {error.Message}");
        Assert.IsNotAssignableFrom<OperationCanceledException>(error);
        if (error is TerminalException terminalError)
        {
            // TerminalClient distinguishes a clean EOF (the dispatcher closed the refused channel) from an I/O
            // failure on that channel. Both are the watch-mode refusal; nothing else is.
            Assert.True(
                terminalError.Message is "The host closed the channel." or "The connection to the host failed.",
                $"Unexpected terminal failure: {terminalError.Message}");
        }
    }

    private static async Task WaitForScreenAsync(TerminalSession session, string expectedText)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!session.Emulator.ScreenText().Contains(expectedText, StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for '{expectedText}' on terminal screen.");
            await Task.Delay(50, Ct);
        }
    }

    private sealed class PermissiveUserContextGuard : IUserContextGuard
    {
        public string? GetRefusalReason() => null;
    }

    private sealed class TestPtyHost : IPtyHost
    {
        private readonly List<TestPtySession> _started = [];

        public IReadOnlyList<TestPtySession> StartedSessions
        {
            get
            {
                lock (_started)
                {
                    return [.. _started];
                }
            }
        }

        public IReadOnlyList<string> GetAvailableShells() => ["bash", "sh", "custom"];

        public IPtySession Start(PtyStartOptions options)
        {
            var session = new TestPtySession(options.Shell ?? "bash", options.Columns, options.Rows);
            lock (_started)
            {
                _started.Add(session);
            }

            return session;
        }
    }

    private sealed class TestPtySession : IPtySession
    {
        private readonly AnonymousPipeServerStream _toShell = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _shellInput;
        private readonly AnonymousPipeClientStream _shellOutput;
        private readonly AnonymousPipeServerStream _fromShell = new(PipeDirection.In);
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private readonly Task _run;

        public TestPtySession(string shell, int columns, int rows)
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
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch
            {
                // Expected when stream breaks
            }

            _exit.TrySetResult(0);
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
            try
            {
                int read;
                while ((read = await _shellInput.ReadAsync(buffer)) > 0)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, read);
                    var formatted = text
                        .Replace("\u001b", "\\e", StringComparison.Ordinal)
                        .Replace("\u0003", "^C", StringComparison.Ordinal)
                        .Replace("\r", "\r\n", StringComparison.Ordinal);
                    Write(formatted);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
