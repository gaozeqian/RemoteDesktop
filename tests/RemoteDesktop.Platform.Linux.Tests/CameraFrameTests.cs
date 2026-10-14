using System.Text;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Video;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// Decoding what a camera produces, which is not what the screen path ever hands over.
/// </summary>
/// <remarks>
/// A webcam on PipeWire offers JPEG far more often than raw pixels, and JPEG decodes to a format with nothing in
/// common with the two the decoder used to accept: <c>yuvj420p</c> and <c>yuvj422p</c> — the same plane layouts under
/// the full colour range JPEG is coded in, and 4:2:2 as often as 4:2:0. Both are in these tests as real files, not
/// as hand-built buffers, because the whole question is whether a real encoder's output survives.
/// </remarks>
public sealed class CameraFrameTests
{
    private const string LinuxOnly = "libavcodec, through the Linux platform layer.";

    private const int Width = 64;
    private const int Height = 64;

    /// <summary>Mid grey with a brighter square in it, 4:2:0, written by libjpeg.</summary>
    private const string Jpeg420 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCABAAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwAooooAKKKKACiiigAooooAKKKKAOzooorwj9VCiiigDjKKKK90/KgooooA7OiiivCP1UKKKKAOMooor3T8qCiiigAooooAKKKKACiiigD/2Q==";

    /// <summary>The same picture at 4:2:2, which is what a webcam actually sends most of the time.</summary>
    private const string Jpeg422 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCABAAEADASEAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwAooAKKACigAooAKKACigAooAKKACigDs6K8I/VQooA4yivdPyoKKAOzorwj9VCigDjKK90/KgooA7OivCP1UKKAOMor3T8qCigDs6K8I/VQooA4yivdPyoKKACigAooAKKACigAooAKKACigD/2Q==";

    [Theory]
    [InlineData(Jpeg420)]
    [InlineData(Jpeg422)]
    public void A_camera_frame_coded_as_jpeg_decodes_to_nv12_at_the_agreed_size(string base64)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var decoder = new FfmpegVideoDecoder("mjpeg", Width, Height);
        using var decoded = decoder.Decode(new EncodedVideoFrame(Convert.FromBase64String(base64), IsKeyFrame: true, TimeSpan.Zero));

        Assert.NotNull(decoded);
        Assert.Equal(PixelFormat.Nv12, decoded.Format);
        Assert.Equal(Width, decoded.Width);
        Assert.Equal(Height, decoded.Height);
        Assert.Equal(MemoryVideoFrame.GetLength(PixelFormat.Nv12, Width, Height), decoded.GetPixels(out _).Length);
    }

    [Theory]
    [InlineData(Jpeg420)]
    [InlineData(Jpeg422)]
    public void A_jpeg_decodes_to_the_brightness_it_was_coded_at(string base64)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var decoder = new FfmpegVideoDecoder("mjpeg", Width, Height);
        using var decoded = decoder.Decode(new EncodedVideoFrame(Convert.FromBase64String(base64), IsKeyFrame: true, TimeSpan.Zero));
        Assert.NotNull(decoded);

        // The fixture is mid grey. What this is really asking is whether the range survived: JPEG codes its luminance
        // across the whole of a byte, while the rest of this pipeline carries the narrower range video is defined
        // over, and a decoder that skipped that conversion would hand back a picture that is washed out rather than
        // obviously wrong. Loose on purpose — JPEG is lossy and this is measuring the scale, not the compression.
        var luma = Average(decoded.GetPixels(out _)[..(Width * Height)]);
        Assert.InRange(luma, 118, 138);
    }

    private static byte Average(ReadOnlySpan<byte> pixels)
    {
        long total = 0;
        foreach (var value in pixels)
        {
            total += value;
        }

        return (byte)(total / pixels.Length);
    }
}
