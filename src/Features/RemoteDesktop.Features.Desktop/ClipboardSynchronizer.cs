using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Protocol.Clipboard;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Features.Desktop;

/// <summary>
/// Keeps two clipboards in step over one stream. Both ends run the same code: each pushes its own clipboard changes
/// to the other and applies what arrives. Content that was just applied is remembered by hash, so it is not sent
/// straight back (which would ping-pong forever).
/// </summary>
/// <remarks>
/// Only changes travel: whatever was on the clipboard when the session began is never sent, since the clipboard often
/// holds something private that was meant for nobody.
/// </remarks>
public sealed partial class ClipboardSynchronizer(IClipboard clipboard, ILogger<ClipboardSynchronizer> logger)
{
    /// <summary>Largest frame accepted from the other side; a PNG plus protobuf overhead fits.</summary>
    public const int MaxFrameBytes = MaxImageBytes + (64 * 1024);

    private const int MaxTextBytes = 4 * 1024 * 1024;
    private const int MaxImageBytes = 8 * 1024 * 1024;

    // Copying often fires several change notifications in a row (one per clipboard format being set).
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private readonly Lock _lock = new();
    private string? _lastHash;

    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => changed.Writer.TryWrite(true);

        clipboard.Changed += OnChanged;
        try
        {
            var send = SendLoopAsync(stream, changed.Reader, stop.Token);
            var receive = ReceiveLoopAsync(stream, stop.Token);
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(send, receive).ConfigureAwait(false);
        }
        finally
        {
            clipboard.Changed -= OnChanged;
        }
    }

    private async Task SendLoopAsync(Stream stream, ChannelReader<bool> changed, CancellationToken cancellationToken)
    {
        try
        {
            while (await changed.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
                while (changed.TryRead(out _))
                {
                }

                ClipboardContent? content;
                try
                {
                    content = clipboard.Get();
                }
                catch (IOException ex)
                {
                    LogReadFailed(logger, ex.Message);
                    continue;
                }

                if (content is null || Encode(content) is not var (message, hash))
                {
                    continue;
                }

                lock (_lock)
                {
                    if (hash == _lastHash)
                    {
                        continue;
                    }

                    _lastHash = hash;
                }

                await ProtobufFrames.WriteAsync(stream, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task ReceiveLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (await ProtobufFrames.ReadAsync(stream, ClipboardMessage.Parser, MaxFrameBytes, cancellationToken).ConfigureAwait(false) is { } message)
            {
                if (Decode(message) is not var (content, hash))
                {
                    LogRejected(logger);
                    continue;
                }

                // Remember before applying: setting the clipboard raises a change event of our own.
                lock (_lock)
                {
                    _lastHash = hash;
                }

                try
                {
                    clipboard.Set(content);
                }
                catch (IOException ex)
                {
                    LogWriteFailed(logger, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static (ClipboardMessage Message, string Hash)? Encode(ClipboardContent content)
    {
        switch (content)
        {
            case TextClipboardContent text:
                var bytes = System.Text.Encoding.UTF8.GetBytes(text.Text);
                return bytes.Length is > 0 and <= MaxTextBytes
                    ? (new ClipboardMessage { Text = new ClipboardText { Text = text.Text } }, HashOf(0, bytes))
                    : null;
            case ImageClipboardContent image:
                return image.Png.Length is > 0 and <= MaxImageBytes
                    ? (new ClipboardMessage { Image = new ClipboardImage { Png = UnsafeByteOperations.UnsafeWrap(image.Png) } }, HashOf(1, image.Png.Span))
                    : null;
            default:
                return null;
        }
    }

    // The other side is authenticated but not trusted with our memory: limits are enforced again on arrival.
    private static (ClipboardContent Content, string Hash)? Decode(ClipboardMessage message)
    {
        switch (message.BodyCase)
        {
            case ClipboardMessage.BodyOneofCase.Text:
                var text = message.Text.Text;
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                return bytes.Length is > 0 and <= MaxTextBytes ? (new TextClipboardContent(text), HashOf(0, bytes)) : null;
            case ClipboardMessage.BodyOneofCase.Image:
                var png = message.Image.Png;
                return png.Length is > 0 and <= MaxImageBytes && IsPng(png.Span)
                    ? (new ImageClipboardContent(png.ToByteArray()), HashOf(1, png.Span))
                    : null;
            default:
                return null;
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';

    private static string HashOf(byte kind, ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData([kind]);
        sha.AppendData(data);
        sha.GetHashAndReset(hash);
        return Convert.ToHexString(hash);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read the clipboard: {Reason}")]
    private static partial void LogReadFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not write the clipboard: {Reason}")]
    private static partial void LogWriteFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored clipboard content that was empty, too large or not what it claimed to be.")]
    private static partial void LogRejected(ILogger logger);
}
