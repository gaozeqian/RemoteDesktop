namespace RemoteDesktop.Abstractions.Audio;

public sealed record AudioFormat(int SampleRate, int Channels)
{
    /// <summary>What travels between the devices: 48 kHz stereo, the native rate of Opus.</summary>
    public static AudioFormat Standard { get; } = new(48_000, 2);
}

public interface IAudioCapturerFactory
{
    IAudioCapturer Create();
}

/// <summary>
/// Captures what the host is playing (loopback). Implementations convert whatever the device delivers to
/// <see cref="AudioFormat.Standard"/>, so the rest of the pipeline never deals with device formats.
/// </summary>
public interface IAudioCapturer : IAsyncDisposable
{
    AudioFormat Format { get; }

    /// <summary>
    /// Streams interleaved 32-bit float PCM in chunks of any size. Nothing arrives while the device plays silence
    /// (loopback devices only deliver data when something is rendering).
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<float>> CaptureAsync(CancellationToken cancellationToken);
}

public interface IAudioPlayerFactory
{
    IAudioPlayer Create();
}

public interface IAudioPlayer : IAsyncDisposable
{
    /// <summary>How much audio is queued but not yet played.</summary>
    TimeSpan Buffered { get; }

    Task StartAsync(AudioFormat format);

    /// <summary>Queues interleaved float samples. If the device has fallen behind, the backlog is dropped first so latency stays bounded.</summary>
    void Enqueue(ReadOnlySpan<float> samples);
}
