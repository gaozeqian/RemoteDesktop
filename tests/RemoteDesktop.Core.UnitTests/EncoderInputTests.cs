using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Shared;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The one input check the three H.264 encoders share: the frame has to be NV12 at exactly the configured size.
/// The exception type stays the platform's choice; the condition and the sentence do not.
/// </summary>
public sealed class EncoderInputTests
{
    private static readonly VideoEncoderOptions Options = new(VideoCodec.H264, 8, 8, 30, 1000);

    [Fact]
    public void A_matching_frame_passes()
    {
        using var frame = new MemoryVideoFrame(8, 8, PixelFormat.Nv12, TimeSpan.Zero);

        EncoderInput.Validate(frame, Options, message => new ArgumentException(message));
    }

    [Fact]
    public void A_frame_of_the_wrong_size_names_both_sizes_in_the_platform_exception()
    {
        using var frame = new MemoryVideoFrame(4, 4, PixelFormat.Nv12, TimeSpan.Zero);

        var error = Assert.Throws<InvalidDataException>(
            () => EncoderInput.Validate(frame, Options, message => new InvalidDataException(message)));

        Assert.Contains("8x8", error.Message);
        Assert.Contains("4x4", error.Message);
    }

    [Fact]
    public void A_frame_of_the_wrong_format_is_refused_too()
    {
        using var frame = new MemoryVideoFrame(8, 8, PixelFormat.Bgra32, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(
            () => EncoderInput.Validate(frame, Options, message => new ArgumentException(message)));
    }
}
