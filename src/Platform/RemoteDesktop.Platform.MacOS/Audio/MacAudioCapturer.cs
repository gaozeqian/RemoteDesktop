using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Audio;

/// <summary>Creates capturers of what the machine is playing.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacAudioCapturerFactory : IAudioCapturerFactory
{
    public IAudioCapturer Create() => new MacAudioCapturer();
}

/// <summary>
/// Captures what the host is playing, through ScreenCaptureKit, as interleaved 32-bit float at
/// <see cref="AudioFormat.Standard"/>.
/// </summary>
/// <remarks>
/// <para>
/// The port contract says loopback — "what the host is playing" — and on macOS that is ScreenCaptureKit's system
/// audio, so this shares both the helper and the Screen Recording grant with the display capture. No microphone
/// permission is involved, and the helper excludes this process's own output so the far end's audio does not loop
/// back.
/// </para>
/// <para>
/// The format is fixed rather than converted. The stream is configured for 48 kHz stereo float, which is exactly
/// what the wire carries, so nothing has to resample — and unlike the Windows side there is no device format to
/// normalise, because loopback here is the system mix rather than one output device.
/// </para>
/// <para>
/// Unlike the Windows capturer, this stream does not end when the user switches output devices: ScreenCaptureKit
/// follows the system mix, which is where the sound goes regardless of which device is selected. The stream runs
/// until the caller cancels.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacAudioCapturer : IAudioCapturer
{
    public AudioFormat Format => AudioFormat.Standard;

    public async IAsyncEnumerable<ReadOnlyMemory<float>> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var sink = new MacAudioChunkSink();

        // Its own token, as in the display and camera capturers: leaving the loop has to stop the stream even when
        // the caller never cancels.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Task.Factory.StartNew(
            () => Run(sink, started, stop.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            // Fails here, before the caller has set anything up, when Screen Recording has not been granted.
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            await foreach (var chunk in sink.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await session.ConfigureAwait(false);
            sink.Complete();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Opens the stream, starts it, and then holds it open. A dedicated thread because both native calls block —
    /// ScreenCaptureKit's content lookup waits on a semaphore — and this is an async iterator, where blocking would
    /// stall a pool thread for as long as the lookup takes.
    /// </summary>
    private static void Run(MacAudioChunkSink sink, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        nint handle = 0;
        var sinkHandle = GCHandle.Alloc(sink);

        try
        {
            handle = Create(sinkHandle);
            Start(handle);
            started.TrySetResult();

            cancellationToken.WaitHandle.WaitOne();
        }
        catch (Exception ex)
        {
            // Ignored when the consumer has already been told the stream started; the chunks simply stop.
            started.TrySetException(ex);
        }
        finally
        {
            if (handle != 0)
            {
                // Stop before destroy, so the native queue is quiet before the sink's memory is considered released.
                MacNativeInterop.rd_audio_capture_stop(handle);
                MacNativeInterop.rd_audio_capture_destroy(handle);
            }

            sinkHandle.Free();
        }
    }

    private static unsafe nint Create(GCHandle sink)
    {
        var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
        error[0] = 0;

        var status = MacNativeInterop.rd_audio_capture_create(
            MacNativeInterop.AudioChunkCallback,
            GCHandle.ToIntPtr(sink),
            out var handle,
            error,
            MacNativeInterop.ErrorBufferSize);

        if (status != MacNativeInterop.Ok)
        {
            throw new InvalidOperationException(
                MacNativeInterop.Describe(status, error, MacNativeInterop.ErrorBufferSize));
        }

        return handle;
    }

    private static unsafe void Start(nint handle)
    {
        var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
        error[0] = 0;

        var status = MacNativeInterop.rd_audio_capture_start(handle, error, MacNativeInterop.ErrorBufferSize);
        if (status != MacNativeInterop.Ok)
        {
            throw new InvalidOperationException(
                MacNativeInterop.Describe(status, error, MacNativeInterop.ErrorBufferSize));
        }
    }
}

/// <summary>
/// Receives audio chunks from the native helper's queue and hands them to <see cref="MacAudioCapturer"/>.
/// </summary>
/// <remarks>
/// The helper calls <see cref="Deliver"/> on a serial dispatch queue, so deliveries never overlap; the consumer
/// reads from another thread, which is what the channel is for. The copy is required rather than defensive: the
/// helper reuses one buffer for every chunk, so the samples are only valid for the duration of the callback.
/// </remarks>
internal sealed class MacAudioChunkSink : IDisposable
{
    /// <summary>
    /// 128 chunks, matching the Windows capturer. ScreenCaptureKit delivers roughly every 10 ms, so this is more
    /// than a second of slack — enough for a consumer that stalls briefly to catch up rather than lose audio.
    /// </summary>
    private readonly Channel<float[]> _chunks = Channel.CreateBounded<float[]>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private bool _disposed;

    public ChannelReader<float[]> Reader => _chunks.Reader;

    /// <summary>Called from the helper's queue. Must not throw: it is reached through a reverse P/Invoke.</summary>
    public unsafe void Deliver(RdAudioChunk* chunk)
    {
        if (chunk is null || chunk->Samples is null || chunk->FrameCount <= 0 || chunk->ChannelCount <= 0)
        {
            return;
        }

        // frame_count * channel_count is what the header promises, and it is the product that can overflow, not
        // either factor. A chunk larger than an int is nonsense, so it is dropped rather than truncated.
        var samples = (long)chunk->FrameCount * chunk->ChannelCount;
        if (samples > int.MaxValue)
        {
            return;
        }

        var copy = new float[(int)samples];
        new ReadOnlySpan<float>(chunk->Samples, copy.Length).CopyTo(copy);

        // A write into a completed channel is a no-op that reports false, which is what happens for a chunk that was
        // in flight while the capture was being torn down. The array is simply collected.
        _chunks.Writer.TryWrite(copy);
    }

    /// <summary>Ends the stream. A pending failure is handed to the consumer through the channel.</summary>
    public void Complete(Exception? error = null) => _chunks.Writer.TryComplete(error);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Complete();
    }
}
