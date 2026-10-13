using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Platform.MacOS.Video;

/// <summary>
/// H.264 encoders registered with VideoToolbox: one hardware, one software, so the selector has hardware first
/// with a built-in fallback.
/// </summary>
/// <remarks>
/// <para>
/// Both are reported as supported without probing the machine. That is a deliberate difference from the Media
/// Foundation side, which has to enumerate transforms because a Windows machine may have no hardware encoder at
/// all: VideoToolbox always provides both an "Apple H.264 (HW)" and an "Apple H.264 (SW)" entry, and the failure
/// mode for the one a caller asked for is a dropped configuration, which <see cref="Create"/> turns into a
/// NotSupportedException the selector falls through on.
/// </para>
/// <para>
/// Which one is actually used is not decided here: <see cref="VideoToolboxH264Encoder.Open"/> asks the helper to
/// create a session with or without the hardware specification, and the helper reports back which encoder took it.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class VideoToolboxH264EncoderFactory : IVideoEncoderFactory
{
    public VideoToolboxH264EncoderFactory(bool hardware)
    {
        IsHardware = hardware;
    }

    public string Name => IsHardware ? "VideoToolbox hardware H.264" : "VideoToolbox software H.264";

    public bool IsHardware { get; }

    public int Priority => 0;

    public bool IsSupported(VideoEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Codec == VideoCodec.H264;
    }

    public IVideoEncoder Create(VideoEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var encoder = new VideoToolboxH264Encoder(preferSoftware: !IsHardware, options);
        try
        {
            encoder.Open();
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }
}

/// <summary>Decodes H.264 access units back into NV12 pictures.</summary>
[SupportedOSPlatform("macos")]
public sealed class VideoToolboxH264DecoderFactory : IVideoDecoderFactory
{
    public string Name => "VideoToolbox H.264 decoder";

    public bool IsHardware => true;

    public int Priority => 0;

    public bool IsSupported(VideoCodec codec) => codec == VideoCodec.H264;

    public IVideoDecoder Create(VideoCodec codec, int width, int height)
    {
        if (codec != VideoCodec.H264)
        {
            throw new NotSupportedException($"This backend decodes H.264, not {codec}.");
        }

        var decoder = new VideoToolboxH264Decoder();
        decoder.Open();
        return decoder;
    }
}
