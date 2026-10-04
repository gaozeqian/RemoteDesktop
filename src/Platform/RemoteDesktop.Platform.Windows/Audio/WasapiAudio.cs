using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemoteDesktop.Abstractions.Audio;

namespace RemoteDesktop.Platform.Windows.Audio;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapturerFactory : IAudioCapturerFactory
{
    public IAudioCapturer Create() => new WasapiLoopbackAudioCapturer();
}

/// <summary>
/// Captures everything the default playback device renders (WASAPI loopback) and converts it to 48 kHz stereo float.
/// The stream ends when the device goes away, e.g. the default output was switched to headphones; the caller simply
/// creates a new capturer, which then follows the new default device.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiLoopbackAudioCapturer : IAudioCapturer
{
    private static readonly TimeSpan BufferedLimit = TimeSpan.FromMilliseconds(500);

    public AudioFormat Format => AudioFormat.Standard;

    public async IAsyncEnumerable<ReadOnlyMemory<float>> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chunks = Channel.CreateBounded<float[]>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
        // Loopback cannot follow the default device by itself, so it is pinned to the current one and a watcher ends
        // the capture when the user switches outputs; the caller then starts over on the new device.
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var deviceId = device.ID;
        using var capture = await new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().WithSharedMode().WithEventSync().BuildAsync().ConfigureAwait(false);

        // Device format → float → stereo → 48 kHz. Whatever the device says is normalized here.
        var buffered = new BufferedWaveProvider(capture.WaveFormat, BufferedLimit) { DiscardOnBufferOverflow = true, ReadFully = false };
        var samples = ToStandard(buffered.ToSampleProvider());
        var readBuffer = new float[AudioFormat.Standard.SampleRate * AudioFormat.Standard.Channels / 10];

        capture.DataAvailable += (buffer, flags, _, _) =>
        {
            // A packet flagged silent carries no usable samples.
            if (flags.HasFlag(AudioClientBufferFlags.Silent))
            {
                return;
            }

            buffered.AddSamples(buffer);
            int read;
            while ((read = samples.Read(readBuffer)) > 0)
            {
                chunks.Writer.TryWrite(readBuffer.AsSpan(0, read).ToArray());
            }
        };
        capture.RecordingStopped += (_, e) => chunks.Writer.TryComplete(e.Exception);

        capture.StartRecording();
        using var watcherStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = WatchDefaultDeviceAsync(enumerator, deviceId, chunks.Writer, watcherStop.Token);
        try
        {
            await foreach (var chunk in chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            capture.StopRecording();
            chunks.Writer.TryComplete();
            await watcherStop.CancelAsync().ConfigureAwait(false);
            await watcher.ConfigureAwait(false);
        }
    }

    private static async Task WatchDefaultDeviceAsync(MMDeviceEnumerator enumerator, string deviceId, ChannelWriter<float[]> chunks, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    chunks.TryComplete();
                    return;
                }

                using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (current.ID != deviceId)
                {
                    chunks.TryComplete();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ISampleProvider ToStandard(ISampleProvider source)
    {
        var channels = source.WaveFormat.Channels;
        var stereo = channels switch
        {
            1 => new MonoToStereoSampleProvider(source),
            2 => source,
            _ => new FirstTwoChannelsSampleProvider(source),
        };

        return stereo.WaveFormat.SampleRate == AudioFormat.Standard.SampleRate
            ? stereo
            : new WdlResamplingSampleProvider(stereo, AudioFormat.Standard.SampleRate);
    }

    /// <summary>Surround to stereo: front left and right are kept, everything else is dropped.</summary>
    private sealed class FirstTwoChannelsSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _scratch = [];

        public FirstTwoChannelsSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            var channels = _source.WaveFormat.Channels;
            var frames = buffer.Length / 2;
            if (_scratch.Length < frames * channels)
            {
                _scratch = new float[frames * channels];
            }

            var read = _source.Read(_scratch.AsSpan(0, frames * channels)) / channels;
            for (var i = 0; i < read; i++)
            {
                buffer[i * 2] = _scratch[i * channels];
                buffer[(i * 2) + 1] = _scratch[(i * channels) + 1];
            }

            return read * 2;
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioPlayerFactory : IAudioPlayerFactory
{
    public IAudioPlayer Create() => new WasapiAudioPlayer();
}

/// <summary>Plays float PCM on the default output device through a short buffer; a backlog is dropped, not played late.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioPlayer : IAudioPlayer
{
    /// <summary>When more than this is queued the device has fallen behind; the backlog goes, trading a click for latency.</summary>
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(200);

    private BufferedWaveProvider? _buffer;
    private WasapiPlayer? _output;

    public TimeSpan Buffered => _buffer?.BufferedDuration ?? TimeSpan.Zero;

    public async Task StartAsync(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (_output is not null)
        {
            throw new InvalidOperationException("The player was already started.");
        }

        _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels), TimeSpan.FromSeconds(1))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true, // silence rather than an underrun error when the stream pauses
        };
        // Following the default device means plugging in headphones moves the sound along with it.
        _output = await new WasapiPlayerBuilder().WithDefaultDeviceStreamRouting().WithSharedMode().WithEventSync().WithLatency(60).BuildAsync().ConfigureAwait(false);
        _output.Init(_buffer);
        _output.Play();
    }

    public void Enqueue(ReadOnlySpan<float> samples)
    {
        var buffer = _buffer ?? throw new InvalidOperationException("The player was not started.");
        if (buffer.BufferedDuration > MaxBuffered)
        {
            buffer.ClearBuffer();
        }

        buffer.AddSamples(MemoryMarshal.AsBytes(samples));
    }

    public ValueTask DisposeAsync()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        return ValueTask.CompletedTask;
    }
}
