using Concentus;
using Concentus.Enums;
using RemoteDesktop.Abstractions.Audio;

namespace RemoteDesktop.Media;

/// <summary>Opus encoder for 20 ms frames of interleaved float samples.</summary>
public sealed class OpusAudioEncoder : IDisposable
{
    /// <summary>Audio in one packet. Short enough to keep latency low, long enough to code efficiently.</summary>
    public const int FrameMilliseconds = 20;

    /// <summary>Upper bound of an encoded packet.</summary>
    public const int MaxPacketBytes = 1500;

    private readonly IOpusEncoder _encoder;

    public OpusAudioEncoder(AudioFormat format, int bitrate = 96_000)
    {
        ArgumentNullException.ThrowIfNull(format);

        Format = format;
        FrameSamples = format.SampleRate * FrameMilliseconds / 1000;
        _encoder = OpusCodecFactory.CreateEncoder(format.SampleRate, format.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = bitrate;
        _encoder.UseVBR = true;
    }

    public AudioFormat Format { get; }

    /// <summary>Samples per channel in one frame.</summary>
    public int FrameSamples { get; }

    /// <summary>Interleaved samples (all channels) in one frame, which is what <see cref="Encode"/> takes.</summary>
    public int FrameLength => FrameSamples * Format.Channels;

    /// <returns>The number of bytes written to <paramref name="packet"/>.</returns>
    public int Encode(ReadOnlySpan<float> frame, Span<byte> packet)
    {
        if (frame.Length != FrameLength)
        {
            throw new ArgumentException($"Expected exactly {FrameLength} samples.", nameof(frame));
        }

        return _encoder.Encode(frame, FrameSamples, packet, packet.Length);
    }

    public void Dispose() => _encoder.Dispose();
}

public sealed class OpusAudioDecoder : IDisposable
{
    /// <summary>Longest audio one packet can carry (120 ms).</summary>
    private const int MaxFrameMilliseconds = 120;

    private readonly IOpusDecoder _decoder;
    private readonly int _maxFrameSamples;

    public OpusAudioDecoder(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        Format = format;
        _maxFrameSamples = format.SampleRate * MaxFrameMilliseconds / 1000;
        _decoder = OpusCodecFactory.CreateDecoder(format.SampleRate, format.Channels);
    }

    public AudioFormat Format { get; }

    /// <summary>A buffer big enough for any packet.</summary>
    public float[] CreateBuffer() => new float[_maxFrameSamples * Format.Channels];

    /// <returns>Interleaved samples written to <paramref name="pcm"/> (all channels).</returns>
    /// <exception cref="InvalidDataException">The packet is not valid Opus.</exception>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
    {
        try
        {
            return _decoder.Decode(packet, pcm, _maxFrameSamples, false) * Format.Channels;
        }
        catch (OpusException ex)
        {
            throw new InvalidDataException("Invalid Opus packet.", ex);
        }
    }

    public void Dispose() => _decoder.Dispose();
}

/// <summary>Cuts a stream of arbitrarily sized sample chunks into frames of exactly the size the encoder wants.</summary>
public sealed class AudioFrameAssembler(int frameLength)
{
    private readonly float[] _pending = new float[frameLength];
    private int _filled;

    /// <summary>Adds samples and calls <paramref name="onFrame"/> for every complete frame; a remainder is kept for the next call.</summary>
    public void Add(ReadOnlySpan<float> samples, FrameHandler onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);

        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, frameLength - _filled);
            samples[..take].CopyTo(_pending.AsSpan(_filled));
            _filled += take;
            samples = samples[take..];
            if (_filled == frameLength)
            {
                onFrame(_pending);
                _filled = 0;
            }
        }
    }

    /// <summary>Throws away a partial frame, e.g. after a gap in the capture.</summary>
    public void Reset() => _filled = 0;

    public delegate void FrameHandler(ReadOnlySpan<float> frame);
}
