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

    // Pointer moves are disposable and coalesced. State-changing events must not be: when 1024 of them are queued the
    // caller applies backpressure instead of silently losing a key-up or button-up and leaving the remote desktop stuck.
    private readonly Channel<InputEvent> _queue;
    private readonly Lock _sendGate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    private InputSender(IChannel channel, int queueCapacity)
    {
        _channel = channel;
        _queue = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public static async Task<InputSender> OpenAsync(
        IPeerConnection connection,
        CancellationToken cancellationToken,
        int queueCapacity = 1024)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);

        var channel = await connection.OpenChannelAsync(ChannelType.Input, bidirectional: false, cancellationToken).ConfigureAwait(false);
        var sender = new InputSender(channel, queueCapacity);
        sender._loop = Task.Run(sender.WriteLoopAsync, CancellationToken.None);
        return sender;
    }

    public void Send(InputEvent inputEvent)
    {
        lock (_sendGate)
        {
            if (_queue.Writer.TryWrite(inputEvent))
            {
                return;
            }

            if (inputEvent is PointerMoveEvent)
            {
                // Replace the last queued move with the newest coordinates without crossing key/button transitions.
                // Draining only happens under congestion; reliable events are written back with backpressure.
                var buffered = new List<InputEvent>();
                while (_queue.Reader.TryRead(out var queued))
                {
                    buffered.Add(queued);
                }

                var move = buffered.FindLastIndex(static queued => queued is PointerMoveEvent);
                if (move >= 0)
                {
                    buffered[move] = inputEvent;
                }

                foreach (var queued in buffered)
                {
                    _queue.Writer.WriteAsync(queued, _stop.Token).AsTask().GetAwaiter().GetResult();
                }

                return;
            }

            try
            {
                _queue.Writer.WriteAsync(inputEvent, _stop.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
            {
                // The session closed while the caller was applying backpressure.
            }
        }
    }

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
