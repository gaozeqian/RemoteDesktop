using Google.Protobuf;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Protocol.Terminal;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The terminal's read loop ends the terminal on any failure; ordinary endings are silent, but a bug in a handler (the
/// emulator, usually) or an unexpected transport exception must leave a trace, or the terminal just "ends for no reason".
/// </summary>
public sealed class TerminalClientTests
{
    [Fact]
    public async Task A_failing_output_handler_ends_the_terminal_and_is_logged()
    {
        var logger = new RecordingLogger();
        await using var client = await OpenAsync(new Script(Opened(), Data("hello")) { Then = Ending.Block }, logger);
        var closed = new TaskCompletionSource();
        client.Closed += (_, _) => closed.TrySetResult();
        client.Output += (_, _) => throw new InvalidOperationException("emulator bug");

        client.Start();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.IsType<InvalidOperationException>(warning.Exception);
        Assert.Contains("Output", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_transport_failure_ends_the_terminal_and_is_logged()
    {
        var logger = new RecordingLogger();
        await using var client = await OpenAsync(new Script(Opened()) { Then = Ending.Unexpected }, logger);
        var closed = new TaskCompletionSource();
        client.Closed += (_, _) => closed.TrySetResult();

        client.Start();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.IsType<NotSupportedException>(warning.Exception);
    }

    [Theory]
    [InlineData(Ending.Closed)]
    [InlineData(Ending.ConnectionLost)]
    public async Task An_ordinary_ending_stays_silent(Ending ending)
    {
        var logger = new RecordingLogger();
        await using var client = await OpenAsync(new Script(Opened(), Data("hello")) { Then = ending }, logger);
        var closed = new TaskCompletionSource();
        client.Closed += (_, _) => closed.TrySetResult();

        client.Start();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(logger.Entries);
    }

    public enum Ending
    {
        /// <summary>The host closes the channel cleanly.</summary>
        Closed,

        /// <summary>The connection drops.</summary>
        ConnectionLost,

        /// <summary>The transport throws something nobody planned for.</summary>
        Unexpected,

        /// <summary>Nothing more ever arrives.</summary>
        Block,
    }

    private static Task<TerminalClient> OpenAsync(Script script, ILogger logger) =>
        TerminalClient.OpenAsync(new ScriptedConnection(script), null, 80, 24, TestContext.Current.CancellationToken, logger);

    private static byte[] Opened() => Encode(ProtobufFrames.EnvelopeMessageType, new TerminalEvent { Opened = new TerminalOpened { Shell = "fake" } }.ToByteArray());

    private static byte[] Data(string text) => Encode(TerminalDataFrame.MessageType, System.Text.Encoding.UTF8.GetBytes(text));

    private static byte[] Encode(ushort type, byte[] payload)
    {
        using var buffer = new MemoryStream();
        FrameCodec.WriteAsync(buffer, new Frame(type, payload)).AsTask().GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private sealed class Script(params byte[][] frames)
    {
        public byte[][] Frames { get; } = frames;

        public Ending Then { get; init; }
    }

    private sealed class ScriptedConnection(Script script) : IPeerConnection
    {
        public DeviceId RemoteDevice => default;

        public ConnectionPath Path => ConnectionPath.Direct;

        public string? RelaySessionId => null;

        public string LocalFingerprint => "local";

        public string RemoteFingerprint => "remote";

        public ValueTask<IChannel> OpenChannelAsync(ChannelType type, bool bidirectional, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel>(new ScriptedChannel(type, new ScriptedStream(script)));

        public ValueTask<IChannel?> AcceptChannelAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IChannel?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedChannel(ChannelType type, Stream stream) : IChannel
    {
        public ChannelType Type => type;

        public Stream Stream => stream;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Takes whatever is written; reads return the canned frames and then end the way the script says.</summary>
    private sealed class ScriptedStream(Script script) : Stream
    {
        private readonly Queue<byte[]> _pending = new(script.Frames);
        private byte[] _current = [];
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _current.Length)
            {
                if (!_pending.TryDequeue(out var next))
                {
                    switch (script.Then)
                    {
                        case Ending.Closed:
                            return 0;
                        case Ending.ConnectionLost:
                            throw new IOException("The connection was reset.");
                        case Ending.Unexpected:
                            throw new NotSupportedException("Unplanned.");
                        default:
                            await Task.Delay(Timeout.Infinite, cancellationToken);
                            return 0;
                    }
                }

                _current = next;
                _offset = 0;
            }

            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
