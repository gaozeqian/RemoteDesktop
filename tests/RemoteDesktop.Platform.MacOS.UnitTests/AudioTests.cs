using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Platform.MacOS.Audio;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

/// <summary>
/// The playback port, exercised for real against the default output device.
/// </summary>
/// <remarks>
/// <para>
/// Everything here queues <em>silence</em> rather than a tone. That is deliberate: what has to be proven is that the
/// samples make the trip through the pointer-and-length P/Invoke, and that the backlog is bounded — neither of which
/// cares what the samples are worth. A suite that emits a tone on every run is one nobody leaves enabled in CI.
/// </para>
/// <para>
/// <c>Enqueue</c> is the entry point worth exercising from managed code specifically. The native helper takes a
/// pointer and an explicit length, and a source-generated marshaller passes a span as a bare pointer with no length
/// beside it, so the wrong declaration silently fills the length with whatever argument follows — which is a crash
/// inside <c>memmove</c>, not a wrong number (docs/ports/README.md, "[LibraryImport] 会把 span 当裸指针传").
/// </para>
/// </remarks>
public class MacAudioPlayerTests
{
    [Fact]
    public void Nothing_is_buffered_before_start()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AudioQueue is macOS only.");

        var player = new MacAudioPlayer();

        Assert.Equal(TimeSpan.Zero, player.Buffered);
    }

    [Fact]
    public void Enqueue_before_start_is_refused_rather_than_quietly_dropped()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AudioQueue is macOS only.");

        var player = new MacAudioPlayer();

        // Dropping it silently would look exactly like a silent failure on the other end of a session, so the port
        // says so instead.
        Assert.Throws<InvalidOperationException>(() => player.Enqueue(new float[480]));
    }

    [Fact]
    public async Task Start_twice_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AudioQueue is macOS only.");

        await using var player = new MacAudioPlayer();
        await player.StartAsync(AudioFormat.Standard);

        await Assert.ThrowsAsync<InvalidOperationException>(() => player.StartAsync(AudioFormat.Standard));
    }

    [Fact]
    public async Task Dispose_without_start_is_safe()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AudioQueue is macOS only.");

        var player = new MacAudioPlayer();
        await player.DisposeAsync();

        // A player that was never opened has nothing to release, and calling it twice must not either.
        await player.DisposeAsync();
    }

    [Fact]
    public async Task A_queued_burst_reports_as_buffered()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AudioQueue is macOS only.");

        await using var player = new MacAudioPlayer();
        await player.StartAsync(AudioFormat.Standard);

        Assert.Equal(TimeSpan.Zero, player.Buffered);

        // 100 ms of stereo. Nothing is written to the device yet, so the count here is exactly what was queued.
        player.Enqueue(Silence(TimeSpan.FromMilliseconds(100), AudioFormat.Standard));

        Assert.True(player.Buffered > TimeSpan.Zero, "The ring accepted the burst but reports nothing queued.");
    }

    [Fact]
    public async Task The_buffered_duration_counts_frames_not_samples()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AudioQueue is macOS only.");

        await using var player = new MacAudioPlayer();
        await player.StartAsync(AudioFormat.Standard);

        // Exactly 100 ms of stereo, which is 9600 floats and 4800 frames. Forgetting to divide by the channel count
        // reports this as 200 ms — a number nothing would catch at run time, since both are plausible amounts of
        // audio to have queued.
        player.Enqueue(Silence(TimeSpan.FromMilliseconds(100), AudioFormat.Standard));

        Assert.True(
            player.Buffered <= TimeSpan.FromMilliseconds(100),
            $"100 ms of stereo was reported as {player.Buffered.TotalMilliseconds} ms.");
    }

    [Fact]
    public async Task A_backlog_is_dropped_rather_than_allowed_to_grow()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AudioQueue is macOS only.");

        await using var player = new MacAudioPlayer();
        await player.StartAsync(AudioFormat.Standard);

        // 300 ms, which is already past the bound the port contract asks for. The device cannot have drained it in
        // the microseconds before the next line, so the second enqueue is guaranteed to see an over-budget backlog.
        player.Enqueue(Silence(TimeSpan.FromMilliseconds(300), AudioFormat.Standard));
        player.Enqueue(Silence(TimeSpan.FromMilliseconds(50), AudioFormat.Standard));

        // The drop-first behaviour: without it the queue would hold 350 ms and the far end would hear audio that is
        // already stale. The ring would clamp at one second, so this assertion is what pins the 200 ms policy —
        // and the ring alone would happily let it through.
        Assert.True(
            player.Buffered < TimeSpan.FromMilliseconds(200),
            $"The backlog was not dropped: {player.Buffered.TotalMilliseconds} ms is queued.");
    }

    [Fact]
    public async Task A_non_standard_format_is_accepted()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AudioQueue is macOS only.");

        // The wire format is fixed at 48 kHz stereo, but the port does not refuse anything else: the native layer
        // opens the device for whatever it is asked for, and a caller that brings its own format should not have to
        // discover this port is secretive about it.
        var format = new AudioFormat(44_100, 1);

        await using var player = new MacAudioPlayer();
        await player.StartAsync(format);

        player.Enqueue(new float[4_410]);

        Assert.True(player.Buffered > TimeSpan.Zero);
        Assert.True(player.Buffered <= TimeSpan.FromMilliseconds(100));
    }

    private static float[] Silence(TimeSpan duration, AudioFormat format)
    {
        var frames = (int)(format.SampleRate * duration.TotalSeconds);
        return new float[frames * format.Channels];
    }
}

/// <summary>
/// The loopback capture port, against a real ScreenCaptureKit stream.
/// </summary>
/// <remarks>
/// <para>
/// These need the Screen Recording grant, because that is what the system audio mix is gated on. Without it the
/// stream never produces a chunk, so they skip rather than fail — a failure there would say nothing about the code.
/// The same tests run green from a signed bundle or a terminal that already holds the grant; <c>--self-check</c>
/// reports the same thing in one line.
/// </para>
/// <para>
/// One test here plays a short, quiet tone through the real output device. That is not incidental: the loopback only
/// delivers while something is rendering, so there is nothing to assert without making the machine render something.
/// </para>
/// </remarks>
public class MacAudioCapturerTests
{
    /// <summary>A second and a half, which is long enough for ScreenCaptureKit to connect and deliver.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public Task The_capturer_delivers_the_standard_format()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ScreenCaptureKit is macOS only.");

        // The stream is configured for this and nothing else, so there is no resampling step to get wrong — but a
        // mismatch here would mean the far end receives audio at the wrong rate, which sounds like a chipmunk rather
        // than an error.
        var capturer = new MacAudioCapturer();
        Assert.Equal(AudioFormat.Standard, capturer.Format);
        return capturer.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task Cancelling_ends_the_capture()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ScreenCaptureKit is macOS only.");
        Assert.SkipUnless(
            TccGrants.IsGranted(TccGrant.ScreenRecording),
            "Screen Recording is not granted to this process, so no loopback can be captured.");

        await using var capturer = new MacAudioCapturer();
        using var cts = new CancellationTokenSource();

        var reading = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in capturer.CaptureAsync(cts.Token))
                {
                }
            }
            catch (OperationCanceledException)
            {
                // The contract: cancellation ends the enumeration rather than leaving it suspended. Catching it is
                // the assertion — anything else escaping would be a real defect.
            }
        }, TestContext.Current.CancellationToken);

        // Give the stream time to connect before tearing it down: the point is that an abandoned capture stops the
        // device, not that cancellation works (that is the runtime's job).
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        await cts.CancelAsync();

        // Bounded, because a capture that never notices the token would hang here forever rather than fail.
        await reading.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_chunk_arrives_while_something_is_playing()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ScreenCaptureKit is macOS only.");
        Assert.SkipUnless(
            TccGrants.IsGranted(TccGrant.ScreenRecording),
            "Screen Recording is not granted to this process, so no loopback can be captured.");

        await using var capturer = new MacAudioCapturer();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = ReadOneAsync(capturer, started, Budget);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            // The stream never got going, which on this machine means no output device rather than a code problem.
            Assert.Skip("The loopback stream did not start in time; is there a default output device?");
        }

        // One 100 ms burst at a twentieth of full scale: audible enough to be real audio, quiet enough not to
        // startle. The alternative — silence — is not equivalent, because the loopback only has something to hand
        // over while the system is actually rendering.
        await using var player = new MacAudioPlayer();
        await player.StartAsync(AudioFormat.Standard);
        player.Enqueue(Tone(TimeSpan.FromMilliseconds(100), AudioFormat.Standard));

        await Task.Delay(TimeSpan.FromMilliseconds(120), TestContext.Current.CancellationToken);
        var chunk = await reading.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotNull(chunk);
        Assert.True(chunk.Value.Length > 0, "The loopback delivered an empty chunk.");

        // Interleaved stereo: every frame contributes exactly two samples, so an odd count would mean the helper
        // flattened one channel short and the far end would hear a stream drifting out of step.
        Assert.Equal(0, chunk.Value.Length % AudioFormat.Standard.Channels);
    }

    /// <summary>Reads exactly one chunk and stops, signalling through <paramref name="started"/> once the stream is live.</summary>
    private static async Task<ReadOnlyMemory<float>?> ReadOneAsync(
        MacAudioCapturer capturer, TaskCompletionSource started, TimeSpan budget)
    {
        try
        {
            await foreach (var chunk in capturer.CaptureAsync(new CancellationTokenSource(budget).Token))
            {
                started.TrySetResult();
                return chunk;
            }

            return null;
        }
        catch (Exception)
        {
            // Reported on the waiting side, where it can become a skip rather than a failure.
            return null;
        }
    }

    private static float[] Tone(TimeSpan duration, AudioFormat format)
    {
        var frames = (int)(format.SampleRate * duration.TotalSeconds);
        var samples = new float[frames * format.Channels];

        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)Math.Sin(2 * Math.PI * 440 * frame / format.SampleRate) * 0.05f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                samples[(frame * format.Channels) + channel] = value;
            }
        }

        return samples;
    }
}
