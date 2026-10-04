using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

/// <summary>Controller side of clipboard sharing: opens the channel and keeps both clipboards in step until disposed or the host ends it.</summary>
public sealed class ClipboardShare : IAsyncDisposable
{
    private readonly IChannel _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    private ClipboardShare(IChannel channel, ClipboardSynchronizer synchronizer)
    {
        _channel = channel;
        _run = synchronizer.RunAsync(channel.Stream, _stop.Token);
    }

    public static async Task<ClipboardShare> StartAsync(IPeerConnection connection, IClipboard clipboard, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(loggers);

        var channel = await connection.OpenChannelAsync(ChannelType.Clipboard, bidirectional: true, cancellationToken).ConfigureAwait(false);
        return new ClipboardShare(channel, new ClipboardSynchronizer(clipboard, loggers.CreateLogger<ClipboardSynchronizer>()));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _run.ConfigureAwait(false);
        _stop.Dispose();
    }
}
