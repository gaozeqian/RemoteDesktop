namespace RemoteDesktop.Abstractions.Media;

/// <summary>
/// One implementation per backend (hardware MFT, software MFT, VideoToolbox...). Supported hardware factories are
/// tried before software ones, then by <see cref="Priority"/>, which yields hardware first with software fallback.
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
    /// <summary>Backend description for diagnostics, e.g. "NVIDIA H.264 Encoder MFT".</summary>
    string Name { get; }

    bool IsHardware { get; }

    VideoEncoderOptions Options { get; }

    /// <summary>
    /// Encodes an NV12 frame of exactly <see cref="VideoEncoderOptions.Width"/> × <see cref="VideoEncoderOptions.Height"/>.
    /// Returns every access unit that became ready, in order: usually one, none while the encoder is buffering, or
    /// several when a late output arrives together with the current one. All of them must be sent.
    /// </summary>
    IReadOnlyList<EncodedVideoFrame> Encode(VideoFrame frame, bool forceKeyFrame);

    /// <summary>Adjusts bitrate at runtime in response to network feedback.</summary>
    void SetBitrate(int bitrateKbps);
}

public interface IVideoDecoderFactory
{
    string Name { get; }

    bool IsHardware { get; }

    int Priority { get; }

    bool IsSupported(VideoCodec codec);

    IVideoDecoder Create(VideoCodec codec, int width, int height);
}

public interface IVideoDecoder : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Decodes one access unit into an NV12 frame of the stream size. Returns <c>null</c> when no picture is ready yet.
    /// The caller owns (and must dispose) the returned frame.
    /// </summary>
    /// <exception cref="InvalidDataException">The bitstream cannot be decoded; request a key frame.</exception>
    VideoFrame? Decode(EncodedVideoFrame frame);
}
