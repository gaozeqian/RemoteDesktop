using System.Diagnostics;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Windows.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>
/// Encode → decode round trips through the real Media Foundation transforms. The hardware variant is skipped on
/// machines without a hardware H.264 encoder (e.g. CI runners).
/// </summary>
public class MediaFoundationCodecTests
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int Frames = 30;

    [Fact]
    public void Software_encoder_round_trips_through_the_decoder() => RoundTrip(hardware: false);

    [Fact]
    public void Hardware_encoder_round_trips_through_the_decoder() => RoundTrip(hardware: true);

    private static void RoundTrip(bool hardware)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only.");
        var encoderFactory = new MediaFoundationH264EncoderFactory(hardware);
        var options = new VideoEncoderOptions(VideoCodec.H264, Width, Height, 60, 6000);
        Assert.SkipUnless(encoderFactory.IsSupported(options), $"No {(hardware ? "hardware" : "software")} H.264 encoder on this machine.");

        var decoderFactory = new MediaFoundationH264DecoderFactory();
        Assert.True(decoderFactory.IsSupported(VideoCodec.H264));

        using var encoder = encoderFactory.Create(options);
        using var decoder = decoderFactory.Create(VideoCodec.H264, Width, Height);
        Assert.Equal(hardware, encoder.IsHardware);

        var encodeTimes = new List<double>();
        var decodeTimes = new List<double>();
        var decoded = 0;
        var lastPsnr = 0.0;
        var stopwatch = new Stopwatch();
        for (var i = 0; i < Frames; i++)
        {
            using var source = CreateTestFrame(i);

            stopwatch.Restart();
            var encoded = encoder.Encode(source, forceKeyFrame: i == 0);
            encodeTimes.Add(stopwatch.Elapsed.TotalMilliseconds);

            foreach (var unit in encoded)
            {
                if (decoded == 0)
                {
                    Assert.True(unit.IsKeyFrame, "The first access unit must be a key frame.");
                    Assert.True(H264Bitstream.ContainsParameterSets(unit.Data.Span), "The key frame must carry SPS/PPS.");
                }

                stopwatch.Restart();
                using var picture = decoder.Decode(unit);
                decodeTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
                if (picture is not null)
                {
                    decoded++;
                    Assert.Equal(PixelFormat.Nv12, picture.Format);
                    Assert.Equal(Width, picture.Width);
                    Assert.Equal(Height, picture.Height);
                    // Low-latency output is in input order, so the k-th picture is the k-th source frame.
                    using var expected = CreateTestFrame(decoded - 1);
                    lastPsnr = LumaPsnr(expected, picture);
                }
            }
        }

        TestContext.Current.SendDiagnosticMessage(
            $"{encoder.Name}: encode avg {encodeTimes.Average():0.0} ms (max {encodeTimes.Max():0.0}), " +
            $"decode avg {decodeTimes.DefaultIfEmpty().Average():0.0} ms, decoded {decoded}/{Frames}, PSNR {lastPsnr:0.0} dB");

        // Low-latency mode: nearly every frame comes out immediately (allow the pipeline a couple of frames).
        Assert.True(decoded >= Frames - 3, $"Only {decoded} of {Frames} frames decoded.");
        Assert.True(lastPsnr > 30, $"Picture quality too low: {lastPsnr:0.0} dB.");
    }

    /// <summary>Moving gradient with a sharp edge, so both smooth areas and detail are exercised.</summary>
    private static MemoryVideoFrame CreateTestFrame(int index)
    {
        using var bgra = new MemoryVideoFrame(Width, Height, PixelFormat.Bgra32, TimeSpan.FromMilliseconds(index * 16.6));
        var pixels = bgra.Buffer;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var p = ((y * Width) + x) * 4;
                pixels[p] = (byte)((x + (index * 8)) & 0xFF);
                pixels[p + 1] = (byte)(y * 255 / Height);
                pixels[p + 2] = (byte)(x < Width / 2 ? 230 : 20);
                pixels[p + 3] = 255;
            }
        }

        var nv12 = new MemoryVideoFrame(Width, Height, PixelFormat.Nv12, bgra.Timestamp);
        ColorConversion.BgraToNv12(bgra.Buffer, bgra.Stride, Width, Height, nv12.Buffer, nv12.Stride);
        return nv12;
    }

    private static double LumaPsnr(VideoFrame expected, VideoFrame actual)
    {
        var a = expected.GetPixels(out var strideA);
        var b = actual.GetPixels(out var strideB);
        double sum = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var d = a[(y * strideA) + x] - b[(y * strideB) + x];
                sum += d * d;
            }
        }

        var mse = sum / (Width * Height);
        return mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
    }
}
