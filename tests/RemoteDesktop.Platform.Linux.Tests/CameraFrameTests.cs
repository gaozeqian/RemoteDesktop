using System.Text;
using RemoteDesktop.Abstractions.Media;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Platform.Linux.XdgPortal;
using RemoteDesktop.Platform.Linux.Camera;
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

    [Fact]
    public void Yuyv_luma_is_carried_through_untouched()
    {
        // 4x2, two groups per row, each one Y0 U Y1 V.
        var yuyv = new byte[]
        {
            10, 100, 20, 110, 30, 120, 40, 130,
            50, 100, 60, 110, 70, 120, 80, 130,
        };
        var nv12 = new byte[4 * 2 * 3 / 2];

        CameraFormats.YuyvToNv12(yuyv, 8, 4, 2, nv12, 4);

        // The luminance is the device's own reading of what the sensor saw, not something this pipeline recomputed,
        // so it has to come back byte for byte. That is the whole promise of asking a camera for raw pixels: no
        // matrix, no range, nothing that could be wrong without being visible.
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 }, nv12[..8]);

        // Chroma arrives once per two pixels across and becomes one per two pixels each way, so the two rows that
        // share a sample are averaged — here they agree, so it is the value that went in.
        Assert.Equal(new byte[] { 100, 110, 120, 130 }, nv12[8..12]);
    }

    [Fact]
    public void Yuyv_chroma_from_the_two_rows_is_averaged_and_rounded_up()
    {
        var yuyv = new byte[]
        {
            0, 100, 0, 100, 0, 100, 0, 100,
            0, 101, 0, 101, 0, 101, 0, 101,
        };
        var nv12 = new byte[4 * 2 * 3 / 2];

        CameraFormats.YuyvToNv12(yuyv, 8, 4, 2, nv12, 4);

        // 201/2 is 101, not 100. Truncating would lean the same way every time, which tints a flat surface — and a
        // flat surface is most of what a camera is pointed at.
        Assert.Equal(new byte[] { 101, 101, 101, 101 }, nv12[8..12]);
    }

    [Fact]
    public void Yuyv_rejects_a_buffer_that_could_not_hold_the_frame()
    {
        // One row where two were promised.
        Assert.Throws<ArgumentException>(() => CameraFormats.YuyvToNv12(new byte[8], 8, 4, 2, new byte[12], 4));

        // Odd, which 4:2:0 cannot express.
        Assert.Throws<ArgumentException>(() => CameraFormats.YuyvToNv12(new byte[3 * 2 * 2], 6, 3, 2, new byte[9], 3));
    }

    [Fact]
    public async Task A_machine_with_no_camera_returns_none_without_asking_anybody()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var cancellationToken = TestContext.Current.CancellationToken;

        bool present;
        try
        {
            // Both the connection and the question, because the connection itself is where a machine without a
            // session bus refuses. What this test is about is the behaviour of a machine whose portal answers —
            // which is the machine the product runs on — so a headless runner is the environment being unavailable,
            // not a failure.
            using var connection = new PortalConnection();
            present = await connection.CameraPresentAsync(cancellationToken);
        }
        catch (PortalException error)
        {
            Assert.Skip($"The portals cannot be reached: {error.Message}");
            return;
        }

        Assert.SkipWhen(present, "This machine has a camera, so listing them would legitimately ask about it.");

        var enumerator = new WaylandCameraEnumerator(
            NullLoggerFactory.Instance,
            NullLogger<WaylandCameraEnumerator>.Instance);

        // Bounded, because the failure this guards against is a dialog: without the presence check the call would be
        // sitting inside AccessCamera waiting for somebody to click something, and an unbounded test would hang
        // rather than fail.
        var listing = await Task.Run(enumerator.GetCameras, cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Empty(listing);
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
