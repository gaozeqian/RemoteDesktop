using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Media;

namespace RemoteDesktop.Core.UnitTests;

public class AudioCodecTests
{
    [Fact]
    public void A_tone_survives_the_opus_round_trip()
    {
        var format = AudioFormat.Standard;
        using var encoder = new OpusAudioEncoder(format);
        using var decoder = new OpusAudioDecoder(format);
        var pcm = decoder.CreateBuffer();

        // 1 kHz on the left channel only, one second long.
        var decoded = new List<float>();
        var packetSizes = new List<int>();
        for (var frame = 0; frame < 50; frame++)
        {
            var input = new float[encoder.FrameLength];
            for (var i = 0; i < encoder.FrameSamples; i++)
            {
                input[i * 2] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * ((frame * encoder.FrameSamples) + i) / format.SampleRate);
            }

            var packet = new byte[OpusAudioEncoder.MaxPacketBytes];
            var length = encoder.Encode(input, packet);
            packetSizes.Add(length);
            var samples = decoder.Decode(packet.AsSpan(0, length), pcm);
            Assert.Equal(encoder.FrameLength, samples);
            decoded.AddRange(pcm.AsSpan(0, samples).ToArray());
        }

        // Skip the first frames (codec warm-up) and compare how much of the energy sits at 1 kHz on the left.
        var left = decoded.Where((_, i) => i % 2 == 0).Skip(4800).ToArray();
        var right = decoded.Where((_, i) => i % 2 == 1).Skip(4800).ToArray();
        Assert.True(Magnitude(left, 1000, format.SampleRate) > 10 * Magnitude(left, 3000, format.SampleRate), "The tone must dominate.");
        Assert.True(Rms(left) > 0.25 && Rms(left) < 0.45, $"Level drifted: {Rms(left):0.000}");
        Assert.True(Rms(right) < 0.02, "The silent channel must stay silent.");
        Assert.True(packetSizes.Average() < 300, "A pure tone must compress well.");
    }

    [Fact]
    public void Wrong_frame_sizes_are_rejected_and_garbage_is_reported()
    {
        using var encoder = new OpusAudioEncoder(AudioFormat.Standard);
        using var decoder = new OpusAudioDecoder(AudioFormat.Standard);

        Assert.Throws<ArgumentException>(() => encoder.Encode(new float[100], new byte[OpusAudioEncoder.MaxPacketBytes]));
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new byte[] { 0xFF, 0xFF, 0xFF }, decoder.CreateBuffer()));
    }

    [Fact]
    public void The_assembler_cuts_any_chunking_into_exact_frames()
    {
        var assembler = new AudioFrameAssembler(10);
        var frames = new List<float[]>();
        var next = 0f;

        foreach (var size in new[] { 3, 17, 1, 9, 0, 20 })
        {
            var chunk = Enumerable.Range(0, size).Select(_ => next++).ToArray();
            assembler.Add(chunk, frame => frames.Add(frame.ToArray()));
        }

        Assert.Equal(5, frames.Count); // 50 samples in, five full frames out
        Assert.All(frames, f => Assert.Equal(10, f.Length));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (float)i), frames.SelectMany(f => f));
    }

    [Fact]
    public void A_partial_frame_can_be_discarded()
    {
        var assembler = new AudioFrameAssembler(4);
        var count = 0;

        assembler.Add([1, 2, 3], _ => count++);
        assembler.Reset();
        assembler.Add([1, 2, 3], _ => count++);

        Assert.Equal(0, count);
    }

    private static double Rms(float[] samples) => Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Length);

    // Goertzel: the strength of one frequency in a signal.
    private static double Magnitude(float[] samples, double frequency, int sampleRate)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / sampleRate);
        double s1 = 0, s2 = 0;
        foreach (var sample in samples)
        {
            var s = sample + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s;
        }

        return Math.Sqrt((s1 * s1) + (s2 * s2) - (coefficient * s1 * s2)) / samples.Length;
    }
}
