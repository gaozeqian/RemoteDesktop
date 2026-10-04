using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Audio;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

/// <summary>Controller side of the audio channel: requests the host's audio, decodes it and plays it.</summary>
public sealed partial class AudioPlayback : IAsyncDisposable
{
    private readonly IChannel _channel;
    private readonly IAudioPlayerFactory _players;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private volatile bool _muted;

    private AudioPlayback(IChannel channel, IAudioPlayerFactory players, ILogger logger)
    {
        _channel = channel;
        _players = players;
        _logger = logger;
    }

    /// <summary>While muted, incoming audio is dropped, so unmuting continues live instead of replaying a backlog.</summary>
    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Raised once when the stream ends.</summary>
    public event EventHandler? Closed;

    public static async Task<AudioPlayback> StartAsync(IPeerConnection connection, IAudioPlayerFactory players, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(players);

        var channel = await connection.OpenChannelAsync(ChannelType.Audio, bidirectional: true, cancellationToken).ConfigureAwait(false);
        var playback = new AudioPlayback(channel, players, logger);
        try
        {
            await ProtobufFrames.WriteAsync(channel.Stream, new AudioControl { Request = new AudioRequest() }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await playback.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        playback._loop = Task.Run(playback.ReceiveLoopAsync, CancellationToken.None);
        return playback;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        OpusAudioDecoder? decoder = null;
        IAudioPlayer? player = null;
        float[] pcm = [];
        try
        {
            while (await FrameCodec.ReadAsync(_channel.Stream, cancellationToken: _stop.Token).ConfigureAwait(false) is { } frame)
            {
                if (frame.MessageType == ProtobufFrames.EnvelopeMessageType)
                {
                    var control = AudioControl.Parser.ParseFrom(frame.Payload.Span);
                    if (control.BodyCase == AudioControl.BodyOneofCase.StreamInfo && control.StreamInfo.Codec == "opus")
                    {
                        // Only the standard format is supported; anything else would need resampling here.
                        var format = new AudioFormat((int)control.StreamInfo.SampleRate, (int)control.StreamInfo.Channels);
                        if (format != AudioFormat.Standard)
                        {
                            throw new InvalidDataException($"Unsupported audio format {format}.");
                        }

                        decoder?.Dispose();
                        decoder = new OpusAudioDecoder(format);
                        pcm = decoder.CreateBuffer();
                        if (player is null)
                        {
                            player = _players.Create();
                            await player.StartAsync(format).ConfigureAwait(false);
                        }
                    }

                    continue;
                }

                if (frame.MessageType != AudioDataFrame.MessageType || decoder is null || player is null || _muted)
                {
                    continue;
                }

                try
                {
                    var samples = decoder.Decode(frame.Payload.Span, pcm);
                    player.Enqueue(pcm.AsSpan(0, samples));
                }
                catch (InvalidDataException ex)
                {
                    LogBadPacket(_logger, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException
                                       or Google.Protobuf.InvalidProtocolBufferException)
        {
        }
#pragma warning disable CA1031 // A broken sound device must not take the session down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPlaybackFailed(_logger, ex);
        }
        finally
        {
            decoder?.Dispose();
            if (player is not null)
            {
                await player.DisposeAsync().ConfigureAwait(false);
            }

            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped an undecodable audio packet: {Reason}")]
    private static partial void LogBadPacket(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio playback stopped.")]
    private static partial void LogPlaybackFailed(ILogger logger, Exception exception);
}
