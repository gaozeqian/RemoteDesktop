namespace RemoteDesktop.Abstractions.Audio;

public sealed record AudioFormat(int SampleRate, int Channels);

/// <summary>Captures what the host is playing (loopback).</summary>
public interface IAudioCapturer : IAsyncDisposable
{
    AudioFormat Format { get; }

    /// <summary>Streams interleaved 32-bit float PCM chunks.</summary>
    IAsyncEnumerable<ReadOnlyMemory<float>> CaptureAsync(CancellationToken cancellationToken);
}

public interface IAudioPlayer : IAsyncDisposable
{
    void Start(AudioFormat format);

    void Enqueue(ReadOnlySpan<float> samples);
}
