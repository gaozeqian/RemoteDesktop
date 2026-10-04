using Google.Protobuf;
using System.Threading.Channels;
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
public sealed class TerminalClient : IAsyncDisposable
{
    private const int MaxEventBytes = 64 * 1024;

    private readonly IChannel _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Frame> _outgoing = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private Task? _reader;
    private int _started;
    private int _closed;

    private TerminalClient(IChannel channel, string shell)
    {
        _channel = channel;
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
                await ProtobufFrames.WriteAsync(channel.Stream, new TerminalRequest { ListShells = new ListShellsRequest() }, cancellationToken).ConfigureAwait(false);
                var answer = await ReadEventAsync(channel.Stream, cancellationToken).ConfigureAwait(false);
                return answer.BodyCase switch
                {
                    TerminalEvent.BodyOneofCase.Shells => [.. answer.Shells.Shells],
                    TerminalEvent.BodyOneofCase.Failed => throw Failure(answer.Failed),
                    _ => throw new TerminalException(TerminalError.Unsupported, "The host answered unexpectedly."),
                };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new TerminalException("The connection to the host failed.", ex);
            }
        }
    }

    /// <summary>Starts a shell on the host.</summary>
    /// <param name="shell">One of the names from <see cref="ListShellsAsync"/>; <c>null</c> for the host's default.</param>
    /// <exception cref="TerminalException">The host refused or could not start the shell.</exception>
    public static async Task<TerminalClient> OpenAsync(IPeerConnection connection, string? shell, int columns, int rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var channel = await connection.OpenChannelAsync(ChannelType.Terminal, bidirectional: true, cancellationToken).ConfigureAwait(false);
        try
        {
            await ProtobufFrames.WriteAsync(
                channel.Stream,
                new TerminalRequest { Open = new OpenTerminal { Shell = shell ?? string.Empty, Columns = (uint)columns, Rows = (uint)rows } },
                cancellationToken).ConfigureAwait(false);
            var answer = await ReadEventAsync(channel.Stream, cancellationToken).ConfigureAwait(false);
            switch (answer.BodyCase)
            {
                case TerminalEvent.BodyOneofCase.Opened:
                    return new TerminalClient(channel, answer.Opened.Shell);
                case TerminalEvent.BodyOneofCase.Failed:
                    throw Failure(answer.Failed);
                default:
                    throw new TerminalException(TerminalError.Unsupported, "The host answered unexpectedly.");
            }
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

    /// <summary>Queues input for the shell (keystrokes, pasted text); large pastes are split into frames.</summary>
    public void Send(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var piece = data[..Math.Min(data.Length, TerminalDataFrame.MaxLength)];
            _outgoing.Writer.TryWrite(new Frame(TerminalDataFrame.MessageType, piece.ToArray()));
            data = data[piece.Length..];
        }
    }

    public void Resize(int columns, int rows) =>
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
                    Output?.Invoke(this, frame.Payload);
                }
                else if (frame.MessageType == ProtobufFrames.EnvelopeMessageType)
                {
                    var message = TerminalEvent.Parser.ParseFrom(frame.Payload.Span);
                    if (message.BodyCase == TerminalEvent.BodyOneofCase.Exited)
                    {
                        Exited?.Invoke(this, message.Exited.ExitCode);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or Google.Protobuf.InvalidProtocolBufferException)
        {
        }
#pragma warning disable CA1031 // The transport may throw its own exception types; the terminal is over either way.
        catch (Exception)
#pragma warning restore CA1031
        {
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
        catch (Exception)
#pragma warning restore CA1031
        {
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
}
