using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

public class ColorConversionTests
{
    [Theory]
    [InlineData(4, 4)]
    [InlineData(640, 360)] // large enough for the parallel path
    public void Bgra_round_trips_through_nv12_within_rounding(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            // Uniform colour per 2x2 block: 4:2:0 subsampling then loses nothing and only rounding remains.
            var x = i % width / 2;
            var y = i / width / 2;
            bgra[(i * 4) + 0] = (byte)(40 + (x * 7 % 150));
            bgra[(i * 4) + 1] = (byte)(60 + (y * 5 % 120));
            bgra[(i * 4) + 2] = (byte)(200 - (x * 3 % 100));
            bgra[(i * 4) + 3] = 255;
        }

        var nv12 = new byte[width * height * 3 / 2];
        var back = new byte[bgra.Length];
        ColorConversion.BgraToNv12(bgra, width * 4, width, height, nv12, width);
        ColorConversion.Nv12ToBgra(nv12, width, width, height, back, width * 4);

        var maxError = 0;
        for (var i = 0; i < bgra.Length; i++)
        {
            maxError = Math.Max(maxError, Math.Abs(bgra[i] - back[i]));
        }

        Assert.True(maxError <= 6, $"Max channel error {maxError}.");
    }

    [Fact]
    public void Black_and_white_map_to_limited_range_luma()
    {
        var bgra = new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255 };
        var nv12 = new byte[4 * 2 * 3 / 2];

        ColorConversion.BgraToNv12(bgra, 16, 4, 2, nv12, 4);

        Assert.Equal(16, nv12[0]);
        Assert.Equal(235, nv12[2]);
        Assert.Equal(128, nv12[8]); // neutral chroma
        Assert.Equal(128, nv12[9]);
    }

    [Fact]
    public void Odd_dimensions_are_rejected() =>
        Assert.Throws<ArgumentException>(() => ColorConversion.BgraToNv12(new byte[3 * 2 * 4], 12, 3, 2, new byte[9], 3));

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

        ColorConversion.YuyvToNv12(yuyv, 8, 4, 2, nv12, 4);

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

        ColorConversion.YuyvToNv12(yuyv, 8, 4, 2, nv12, 4);

        // 201/2 is 101, not 100. Truncating would lean the same way every time, which tints a flat surface — and a
        // flat surface is most of what a camera is pointed at.
        Assert.Equal(new byte[] { 101, 101, 101, 101 }, nv12[8..12]);
    }

    [Fact]
    public void Yuyv_rejects_a_buffer_that_could_not_hold_the_frame()
    {
        // One row where two were promised.
        Assert.Throws<ArgumentException>(() => ColorConversion.YuyvToNv12(new byte[8], 8, 4, 2, new byte[12], 4));

        // Odd, which 4:2:0 cannot express.
        Assert.Throws<ArgumentException>(() => ColorConversion.YuyvToNv12(new byte[3 * 2 * 2], 6, 3, 2, new byte[9], 3));
    }
}

public class H264BitstreamTests
{
    [Fact]
    public void Detects_idr_and_parameter_sets()
    {
        byte[] keyFrame = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 1, 0x65, 4, 5];
        byte[] deltaFrame = [0, 0, 0, 1, 0x41, 9, 9];

        Assert.Equal([7, 8, 5], H264Bitstream.GetNalTypes(keyFrame));
        Assert.True(H264Bitstream.IsKeyFrame(keyFrame));
        Assert.True(H264Bitstream.ContainsParameterSets(keyFrame));
        Assert.False(H264Bitstream.IsKeyFrame(deltaFrame));
        Assert.False(H264Bitstream.ContainsParameterSets(deltaFrame));
    }
}

public class BitrateControllerTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(100); // > 2 frame intervals at 60 fps

    [Fact]
    public void Congestion_lowers_the_rate_with_a_cooldown()
    {
        var controller = new BitrateController(10_000, 1_000, 20_000, 60);

        Assert.Equal(7_500, controller.OnFrameSent(Slow, TimeSpan.FromSeconds(1)));
        Assert.Null(controller.OnFrameSent(Slow, TimeSpan.FromSeconds(1.1))); // within cooldown
        Assert.Equal(5_625, controller.OnFrameSent(Slow, TimeSpan.FromSeconds(1.6)));
    }

    [Fact]
    public void Calm_periods_raise_the_rate_up_to_the_maximum()
    {
        var controller = new BitrateController(10_000, 1_000, 12_000, 60);

        Assert.Null(controller.OnFrameSent(Fast, TimeSpan.FromSeconds(1)));
        Assert.Equal(11_600, controller.OnFrameSent(Fast, TimeSpan.FromSeconds(2.5)));
        Assert.Equal(12_000, controller.OnFrameSent(Fast, TimeSpan.FromSeconds(5)));
        Assert.Null(controller.OnFrameSent(Fast, TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void Rate_never_drops_below_the_minimum()
    {
        var controller = new BitrateController(1_200, 1_000, 5_000, 60);

        controller.OnFrameSent(Slow, TimeSpan.FromSeconds(1));
        controller.OnFrameSent(Slow, TimeSpan.FromSeconds(2));

        Assert.Equal(1_000, controller.CurrentKbps);
    }
}

public class DesktopWireFormatTests
{
    [Fact]
    public async Task Video_data_frame_round_trips()
    {
        using var stream = new MemoryStream();
        var capturedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, 123, TimeSpan.Zero);
        await VideoDataFrame.WriteAsync(stream, new EncodedVideoFrame(new byte[] { 0, 0, 1, 0x65 }, true, TimeSpan.Zero), capturedAt, TestContext.Current.CancellationToken);
        stream.Position = 0;

        var frame = await FrameCodec.ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        var (encoded, time) = VideoDataFrame.Parse(frame!.Value);

        Assert.True(encoded.IsKeyFrame);
        Assert.Equal(new byte[] { 0, 0, 1, 0x65 }, encoded.Data.ToArray());
        Assert.Equal(capturedAt, time);
    }

    [Fact]
    public void Input_events_round_trip_through_messages()
    {
        InputEvent[] events =
        [
            new PointerMoveEvent("d1", 0.25, 0.75),
            new PointerButtonEvent("d1", 0.5, 0.5, Abstractions.Input.PointerButton.Right, true),
            new PointerWheelEvent("d1", 0.1, 0.2, 0, -120),
            new KeyEvent(0x1D, true, false),
            new TextInputEvent("你好"),
        ];

        foreach (var inputEvent in events)
        {
            Assert.Equal(inputEvent, InputMessages.ToEvent(InputMessages.ToMessage(inputEvent)));
        }
    }

    [Fact]
    public void Untrusted_input_is_sanitized()
    {
        var move = InputMessages.ToEvent(new InputMessage { PointerMove = new PointerMove { X = double.NaN, Y = 7 } });
        Assert.Equal(new PointerMoveEvent(string.Empty, 0, 1), move);

        Assert.Null(InputMessages.ToEvent(new InputMessage { Key = new Key { ScanCode = 0x1234 } }));
        Assert.Null(InputMessages.ToEvent(new InputMessage { Text = new Text { Value = new string('x', 1000) } }));
        Assert.Null(InputMessages.ToEvent(new InputMessage()));
    }
}
