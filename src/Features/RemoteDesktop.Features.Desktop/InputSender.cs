using System.Threading.Channels;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

/// <summary>
/// Controller side of the input channel. Events are queued from the UI thread and written by a background loop;
/// runs of pointer moves are coalesced to the latest position so a slow link never builds up a backlog of motion.
/// </summary>
public sealed class InputSender : IAsyncDisposable
{
    private readonly IChannel _channel;
    private readonly Channel<InputEvent> _queue = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    private InputSender(IChannel channel) => _channel = channel;

    public static async Task<InputSender> OpenAsync(IPeerConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var channel = await connection.OpenChannelAsync(ChannelType.Input, bidirectional: false, cancellationToken).ConfigureAwait(false);
        var sender = new InputSender(channel);
        sender._loop = Task.Run(sender.WriteLoopAsync, CancellationToken.None);
        return sender;
    }

    public void Send(InputEvent inputEvent) => _queue.Writer.TryWrite(inputEvent);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var inputEvent))
                {
                    // Skip a move when another move is already queued behind it.
                    if (inputEvent is PointerMoveEvent && reader.TryPeek(out var next) && next is PointerMoveEvent)
                    {
                        continue;
                    }

                    await ProtobufFrames.WriteAsync(_channel.Stream, InputMessages.ToMessage(inputEvent), _stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Refused (watch mode) or connection lost; further input is dropped.
            _queue.Writer.TryComplete();
        }
    }
}
