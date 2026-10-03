namespace RemoteDesktop.Abstractions.Media;

public enum VideoCodec
{
    H264,
    H265,
}

public enum EncoderPreference
{
    /// <summary>Prefer lower latency and higher frame rate.</summary>
    Smoothness,

    /// <summary>Prefer image clarity (text, CAD) at the cost of frame rate.</summary>
    Quality,
}

public sealed record VideoEncoderOptions(
    VideoCodec Codec,
    int Width,
    int Height,
    int FrameRate,
    int BitrateKbps,
    EncoderPreference Preference = EncoderPreference.Smoothness);

/// <param name="Data">Annex-B bitstream for one access unit.</param>
public sealed record EncodedVideoFrame(ReadOnlyMemory<byte> Data, bool IsKeyFrame, TimeSpan Timestamp);
