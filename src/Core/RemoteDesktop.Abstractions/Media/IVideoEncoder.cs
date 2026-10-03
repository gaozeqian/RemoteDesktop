namespace RemoteDesktop.Abstractions.Media;

/// <summary>
/// One implementation per backend (hardware MFT, FFmpeg, VideoToolbox...). The supported factory with the highest
/// <see cref="Priority"/> wins, which yields hardware first with software fallback.
/// </summary>
public interface IVideoEncoderFactory
{
    string Name { get; }

    bool IsHardware { get; }

    int Priority { get; }

    bool IsSupported(VideoEncoderOptions options);

    IVideoEncoder Create(VideoEncoderOptions options);
}

public interface IVideoEncoder : IDisposable
{
    VideoEncoderOptions Options { get; }

    /// <summary>Encodes a frame. May return <c>null</c> while the encoder is buffering.</summary>
    EncodedVideoFrame? Encode(VideoFrame frame, bool forceKeyFrame);

    /// <summary>Adjusts bitrate at runtime in response to network feedback.</summary>
    void SetBitrate(int bitrateKbps);
}

public interface IVideoDecoderFactory
{
    string Name { get; }

    bool IsHardware { get; }

    int Priority { get; }

    bool IsSupported(VideoCodec codec);

    IVideoDecoder Create(VideoCodec codec);
}

public interface IVideoDecoder : IDisposable
{
    /// <summary>Decodes one access unit. Returns <c>null</c> when no picture is ready yet.</summary>
    VideoFrame? Decode(EncodedVideoFrame frame);
}
