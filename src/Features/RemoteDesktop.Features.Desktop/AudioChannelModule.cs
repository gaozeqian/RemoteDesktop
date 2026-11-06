using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Audio;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Desktop;

/// <summary>
/// Streams what the host is playing to the controller: loopback capture → Opus in 20 ms packets → audio channel.
/// Available in watch mode too (the view-only grant includes audio). When the capture ends because the output
/// device changed, or fails because there is no device, a new capture is started after a short pause, so audio
/// follows the user's headphones and comes back when a device is plugged in.
/// </summary>
public sealed partial class AudioChannelModule(IAudioCapturerFactory capturers, ILogger<AudioChannelModule> logger) : IFeatureModule
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

    public ChannelType ChannelType => ChannelType.Audio;

    public Permissions RequiredPermission => Permissions.Audio;

    public async Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(SetupTimeout);
            var first = await ProtobufFrames.ReadAsync(channel.Stream, AudioControl.Parser, 4096, timeout.Token).ConfigureAwait(false);
            if (first?.BodyCase != AudioControl.BodyOneofCase.Request)
            {
                return;
            }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var controllerGone = WatchControllerAsync(channel.Stream, stop);
        try
        {
            await ProtobufFrames.WriteAsync(
                channel.Stream,
                new AudioControl
                {
                    StreamInfo = new AudioStreamInfo
                    {
                        SampleRate = (uint)AudioFormat.Standard.SampleRate,
                        Channels = (uint)AudioFormat.Standard.Channels,
                        FrameMilliseconds = OpusAudioEncoder.FrameMilliseconds,
                        Codec = "opus",
                    },
                },
                stop.Token).ConfigureAwait(false);

            using var encoder = new OpusAudioEncoder(AudioFormat.Standard);
            var packet = new byte[OpusAudioEncoder.MaxPacketBytes];
            var assembler = new AudioFrameAssembler(encoder.FrameLength);

            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var capturer = capturers.Create();
                    assembler.Reset();
                    await foreach (var chunk in capturer.CaptureAsync(stop.Token).ConfigureAwait(false))
                    {
                        // A frame is written as soon as it is complete; the write is awaited so a slow network
                        // slows the loop down instead of queueing audio without bound.
                        var pending = new List<byte[]>();
                        assembler.Add(chunk.Span, frame => pending.Add(packet.AsSpan(0, encoder.Encode(frame, packet)).ToArray()));
                        foreach (var encoded in pending)
                        {
                            await AudioDataFrame.WriteAsync(channel.Stream, encoded, stop.Token).ConfigureAwait(false);
                        }
                    }
                }
#pragma warning disable CA1031 // No audio device, or the device failed: try again rather than end the session.
                catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
#pragma warning restore CA1031
                {
                    LogCaptureFailed(logger, ex.Message);
                }

                await Task.Delay(RestartDelay, stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await controllerGone.ConfigureAwait(false);
        }
    }

    // The controller sends nothing after its request; the stream ending is how it says goodbye.
    private static async Task WatchControllerAsync(Stream stream, CancellationTokenSource stop)
    {
        try
        {
            while (await ProtobufFrames.ReadAsync(stream, AudioControl.Parser, 4096, stop.Token).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
        }

        await stop.CancelAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio capture ended ({Reason}); retrying.")]
    private static partial void LogCaptureFailed(ILogger logger, string reason);
}
