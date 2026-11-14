using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Platform.Shared;

/// <summary>The input check every H.264 encoder performs before it touches its native backend.</summary>
/// <remarks>
/// The three encoders had the same condition and three slightly different sentences. The exception type stays a
/// choice of the platform — Media Foundation and VideoToolbox report an argument, FFmpeg reports invalid data — so the
/// caller supplies the factory while the condition and the description are written once.
/// </remarks>
internal static class EncoderInput
{
    public static void Validate(VideoFrame frame, VideoEncoderOptions options, Func<string, Exception> failure)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(failure);

        if (frame.Format != PixelFormat.Nv12 || frame.Width != options.Width || frame.Height != options.Height)
        {
            throw failure(
                $"Expected NV12 {options.Width}x{options.Height}, got {frame.Format} {frame.Width}x{frame.Height}.");
        }
    }
}
