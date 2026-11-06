using Google.Protobuf;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Protocol.Terminal;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Terminal;

/// <summary>The host did not open the terminal, and says why.</summary>
public sealed class TerminalException : Exception
{
    public TerminalException()
    {
    }

    public TerminalException(string message)
        : base(message)
    {
    }

    public TerminalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TerminalException(TerminalError error, string message)
        : base(message)
    {
        Error = error;
    }

    public TerminalError Error { get; }
}

/// <summary>
/// Controller side of one terminal channel: raw output comes out through <see cref="Output"/>, raw input goes in through
/// <see cref="Send"/>. It does not interpret the bytes (see <see cref="TerminalSession"/> for that).
/// </summary>
public sealed partial class TerminalClient : IAsyncDisposable
{
    private const int MaxEventBytes = 64 * 1024;

    /// <summary>At most 4 MiB of input may wait for the channel (1024 frames of up to 4 KiB).</summary>
    private const int MaxQueuedFrames = 1024;

    private readonly IChannel _channel;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Frame> _outgoing = Channel.CreateBounded<Frame>(new BoundedChannelOptions(MaxQueuedFrames)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    private readonly Task _writer;
    private Task? _reader;
    private int _started;
    private int _closed;
    private long _droppedFrames;

    private TerminalClient(IChannel channel, string shell, ILogger logger)
    {
        _channel = channel;
        _logger = logger;
        Shell = shell;
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>The shell the host started.</summary>
    public string Shell { get; }

    /// <summary>Raw bytes from the shell, in order. Raised on a background thread; handlers must not block.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? Output;

    /// <summary>The shell ended with this exit code. <see cref="Closed"/> follows.</summary>
    public event EventHandler<int>? Exited;

    /// <summary>Nothing more will arrive: the shell ended, the host closed the channel or the connection was lost.</summary>
    public event EventHandler? Closed;

    /// <summary>Which shells the host offers.</summary>
    public static async Task<IReadOnlyList<string>> ListShellsAsync(IPeerConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var channel = await connection.OpenChannelAsync(ChannelType.Terminal, bidirectional: true, cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            try
            {
                var answer = await RequestTimeout.RunAsync(
                    async token =>
                    {
                        await ProtobufFrames.WriteAsync(channel.Stream, new TerminalRequest { ListShells = new ListShellsRequest() }, token).ConfigureAwait(false);
                        return await ReadEventAsync(channel.Stream, token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
                return answer.BodyCase switch
                {
                    TerminalEvent.BodyOneofCase.Shells => [.. answer.Shells.Shells],
                    TerminalEvent.BodyOneofCase.Failed => throw Failure(answer.Failed),
                    _ => throw new TerminalException(TerminalError.Unsupported, "The host answered unexpectedly."),
                };
            }
            catch (TimeoutException ex)
            {
                throw new TerminalException(ex.Message, ex);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new TerminalException("The connection to the host failed.", ex);
            }
        }
    }

    /// <summary>Starts a shell on the host.</summary>
    /// <param name="shell">One of the names from <see cref="ListShellsAsync"/>; <c>null</c> for the host's default.</param>
    /// <param name="logger">
    /// Where unexpected failures of the read and write loops (and of the <see cref="Output"/> and <see cref="Exited"/>
    /// handlers) are reported; nothing is logged when omitted. Ordinary endings (the host closing, the connection
    /// dropping, disposal) are silent either way.
    /// </param>
    /// <exception cref="TerminalException">The host refused or could not start the shell.</exception>
    public static async Task<TerminalClient> OpenAsync(
        IPeerConnection connection,
        string? shell,
        int columns,
        int rows,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var channel = await connection.OpenChannelAsync(ChannelType.Terminal, bidirectional: true, cancellationToken).ConfigureAwait(false);
        try
        {
            var answer = await RequestTimeout.RunAsync(
                async token =>
                {
                    await ProtobufFrames.WriteAsync(
                        channel.Stream,
                        new TerminalRequest { Open = new OpenTerminal { Shell = shell ?? string.Empty, Columns = (uint)columns, Rows = (uint)rows } },
                        token).ConfigureAwait(false);
                    return await ReadEventAsync(channel.Stream, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            switch (answer.BodyCase)
            {
                case TerminalEvent.BodyOneofCase.Opened:
                    return new TerminalClient(channel, answer.Opened.Shell, logger ?? NullLogger.Instance);
                case TerminalEvent.BodyOneofCase.Failed:
                    throw Failure(answer.Failed);
                default:
                    throw new TerminalException(TerminalError.Unsupported, "The host answered unexpectedly.");
            }
        }
        catch (TimeoutException ex)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new TerminalException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new TerminalException("The connection to the host failed.", ex);
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts delivering output. Separate from opening so that the owner can subscribe first: output that arrives with
    /// nobody listening would be lost.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _reader = Task.Run(ReadAsync);
        }
    }

    /// <summary>Frames dropped because the writer was not keeping up. Exposed for tests and diagnostics.</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>
    /// Queues input for the shell (keystrokes, pasted text); large pastes are split into frames. Returns <c>false</c>
    /// when the bounded queue was full and the rest of this call was dropped: the caller's only backpressure signal.
    /// </summary>
    public bool Send(ReadOnlySpan<byte> data)
    {
        var queued = true;
        while (!data.IsEmpty)
        {
            var piece = data[..Math.Min(data.Length, TerminalDataFrame.MaxLength)];
            if (!_outgoing.Writer.TryWrite(new Frame(TerminalDataFrame.MessageType, piece.ToArray())))
            {
                // Dropping the tail of one input is the bounded-memory trade. It is logged once per client so a
                // session that keeps overflowing is visible without flooding the log frame by frame.
                if (Interlocked.Increment(ref _droppedFrames) == 1)
                {
                    LogInputDropped(_logger);
                }

                queued = false;
                break;
            }

            data = data[piece.Length..];
        }

        return queued;
    }

    public bool Resize(int columns, int rows) =>
        _outgoing.Writer.TryWrite(new Frame(
            ProtobufFrames.EnvelopeMessageType,
            new TerminalRequest { Resize = new ResizeTerminal { Columns = (uint)columns, Rows = (uint)rows } }.ToByteArray()));

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_reader ?? Task.CompletedTask, _writer).ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await FrameCodec.ReadAsync(_channel.Stream, MaxEventBytes * 16, _stop.Token).ConfigureAwait(false) is { } frame)
            {
                if (frame.MessageType == TerminalDataFrame.MessageType)
                {
                    try
                    {
                        Output?.Invoke(this, frame.Payload);
                    }
#pragma warning disable CA1031 // A handler (the emulator, typically) can fail in any way; the terminal ends, and the cause is logged.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogHandlerFailed(_logger, ex, nameof(Output));
                        return;
                    }
                }
                else if (frame.MessageType == ProtobufFrames.EnvelopeMessageType)
                {
                    var message = TerminalEvent.Parser.ParseFrom(frame.Payload.Span);
                    if (message.BodyCase == TerminalEvent.BodyOneofCase.Exited)
                    {
                        try
                        {
                            Exited?.Invoke(this, message.Exited.ExitCode);
                        }
#pragma warning disable CA1031 // As for Output: the terminal ends, and the cause is logged.
                        catch (Exception ex)
#pragma warning restore CA1031
                        {
                            LogHandlerFailed(_logger, ex, nameof(Exited));
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or Google.Protobuf.InvalidProtocolBufferException)
        {
        }
#pragma warning disable CA1031 // The transport may throw its own exception types; the terminal is over either way.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLoopFailed(_logger, ex, "read");
        }
        finally
        {
            RaiseClosed();
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await FrameCodec.WriteAsync(_channel.Stream, frame, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // The transport may throw its own exception types; the reader reports the channel as closed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLoopFailed(_logger, ex, "write");
        }
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task<TerminalEvent> ReadEventAsync(Stream stream, CancellationToken cancellationToken) =>
        await ProtobufFrames.ReadAsync(stream, TerminalEvent.Parser, MaxEventBytes, cancellationToken).ConfigureAwait(false)
        ?? throw new TerminalException("The host closed the channel.");

    private static TerminalException Failure(TerminalFailed failed) => new(failed.Error, failed.Message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The terminal's {Loop} loop failed unexpectedly; the terminal is over.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception, string loop);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A handler of the terminal's {Event} event threw; the terminal is over.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string @event);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The terminal's input queue is full; the tail of an input was dropped.")]
    private static partial void LogInputDropped(ILogger logger);
}
