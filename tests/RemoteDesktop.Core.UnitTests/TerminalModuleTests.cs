using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Protocol.Terminal;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The host side of one terminal channel, played by a fake PTY here. The interesting boundary is the end: closing
/// the channel disposes the shell, and the read that was in flight then fails because its descriptor is gone.
/// </summary>
public sealed class TerminalModuleTests
{
    private static readonly SessionContext TestSession =
        new(SessionId.New(), SessionRole.Host, null!, AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_terminal_that_ends_while_its_output_is_being_read_leaves_no_unobserved_exception()
    {
        var pty = new FakePtyHost();
        var unobserved = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var match = e.Exception.Flatten().InnerExceptions
                .FirstOrDefault(exception => exception is ObjectDisposedException disposed && disposed.ObjectName == pty.Session.OutputMarker);
            if (match is not null)
            {
                unobserved.TrySetResult(match);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await Run_until_the_controller_closes_async(pty);

            // The pumps are no longer referenced. Their exception, if nobody looked at it, is rethrown when the
            // task is finalized — which is exactly what used to end up in crash.log.
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            Assert.False(unobserved.Task.IsCompleted, "The terminal pump left an unobserved exception behind.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task A_failed_opened_response_still_disposes_the_started_terminal()
    {
        var pty = new FakePtyHost();
        var (moduleEnd, controller) = await PipePair.CreateAsync();
        var failing = new FailingWriteStream(moduleEnd);
        var module = new TerminalModule(pty, new AllowEverything(), NullLogger<TerminalModule>.Instance);
        try
        {
            var run = module.HandleAsync(new TestChannel(failing), TestSession, Ct);
            await ProtobufFrames.WriteAsync(
                controller,
                new TerminalRequest { Open = new OpenTerminal { Shell = "fake", Columns = 80, Rows = 24 } },
                Ct);

            await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(1, pty.Session.DisposeCount);
        }
        finally
        {
            await controller.DisposeAsync();
            await failing.DisposeAsync();
        }
    }

    private static async Task Run_until_the_controller_closes_async(FakePtyHost pty)
    {
        var (moduleEnd, controller) = await PipePair.CreateAsync();
        var stop = new CancellationTokenSource();
        var module = new TerminalModule(pty, new AllowEverything(), NullLogger<TerminalModule>.Instance);
        try
        {
            var run = module.HandleAsync(new TestChannel(moduleEnd), TestSession, stop.Token);
            await ProtobufFrames.WriteAsync(
                controller,
                new TerminalRequest { Open = new OpenTerminal { Shell = "fake", Columns = 80, Rows = 24 } },
                Ct);
            var opened = await ProtobufFrames.ReadAsync(controller, TerminalEvent.Parser, cancellationToken: Ct);
            Assert.Equal("fake", opened?.Opened?.Shell);

            await controller.DisposeAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        finally
        {
            await stop.CancelAsync();
            await controller.DisposeAsync();
            stop.Dispose();
        }
    }

    private sealed class AllowEverything : IUserContextGuard
    {
        public string? GetRefusalReason() => null;
    }

    private sealed class FakePtyHost : IPtyHost
    {
        public FakePtySession Session { get; } = new();

        public IReadOnlyList<string> GetAvailableShells() => ["fake"];

        public IPtySession Start(PtyStartOptions options) => Session;
    }

    private sealed class FakePtySession : IPtySession
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingOutputStream OutputStream { get; } = new();

        public Stream Output => OutputStream;

        public string OutputMarker => OutputStream.Marker;

        public int DisposeCount { get; private set; }

        public Stream Input { get; } = new MemoryStream();

        public void Resize(int columns, int rows)
        {
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            OutputStream.Dispose();
            _exit.TrySetResult(-1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get; set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected Opened response failure."));

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Injected write failure.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A read that does not finish until the stream is disposed, like a read parked in the kernel on a PTY. It
    /// deliberately ignores cancellation: disposing the terminal is what unblocks it, and that is the race this
    /// test is about.
    /// </summary>
    private sealed class BlockingOutputStream : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Marker { get; } = Guid.NewGuid().ToString("N");

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(_read.Task);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.TrySetException(new ObjectDisposedException(Marker));
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TestChannel(Stream stream) : IChannel
    {
        public ChannelType Type => ChannelType.Terminal;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private static class PipePair
    {
        public static async Task<(Stream Module, Stream Controller)> CreateAsync()
        {
            var name = $"rd-unit-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
            return (server, client);
        }
    }
}
