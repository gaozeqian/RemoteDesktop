using System.Runtime.InteropServices;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Platform.Linux.Audio;
using PulseApi = RemoteDesktop.Platform.Linux.Audio.Pulse;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// Audio against the sound server actually running on this machine.
/// </summary>
public sealed class PulseAudioTests
{
    private const string LinuxOnly = "PulseAudio, through the Linux platform layer.";

    [Fact]
    public void The_sample_format_constant_is_the_one_the_server_means()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // This is the only value in the binding that could not be read out of a header, because the headers are not
        // installed. Four bytes is what the server means by a 32-bit float; anything else and the capture would be
        // noise that looks like a driver fault.
        Assert.Equal(4u, (uint)PulseApi.SampleSizeOfFormat(PulseApi.SampleFormatFloat32Le));
    }

    [Theory]
    [InlineData(new float[] { 0f, 0f, 0f, 0f }, true)]
    [InlineData(new float[] { 0f, 1e-9f, 0f }, true)]
    [InlineData(new float[] { 0f, 0.001f, 0f }, false)]
    [InlineData(new float[] { -0.5f }, false)]
    public void Silence_is_recognised_and_anything_else_is_not(float[] samples, bool silent)
    {
        // On Windows this is the loopback device's own behaviour: it delivers nothing while nothing is playing. A
        // monitor source delivers silence forever, so without this the host would encode and send nothing at full
        // bitrate for the whole time a controller was connected.
        Assert.Equal(silent, PulseAudioCapturer.IsSilent(samples));
    }

    [Fact]
    public async Task Capture_connects_to_the_output_it_should_watch_and_runs_cleanly()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        await using var capturer = new PulseAudioCapturer();

        Assert.Equal(AudioFormat.Standard, capturer.Format);

        // Reaching the end of this wait without an exception is the assertion: it means the context connected, the
        // monitor was found and the stream reached a usable state. On a quiet machine nothing is yielded, which is
        // the contract rather than a failure — so this waits a moment and then stops of its own accord.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(1));

        var chunks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in capturer.CaptureAsync(stop.Token))
            {
                chunks++;
                Assert.False(chunk.IsEmpty, "A chunk with no samples arrived.");
            }
        });

        _ = chunks;
    }

    [Fact]
    public async Task Playback_connects_accepts_audio_and_reports_what_is_queued()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        await using var player = new PulseAudioPlayer();
        await player.StartAsync(AudioFormat.Standard);

        Assert.Equal(TimeSpan.Zero, player.Buffered);

        // Half a second of a quiet tone.
        var samples = new float[AudioFormat.Standard.SampleRate / 2 * AudioFormat.Standard.Channels];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.05f * MathF.Sin(i * 0.05f);
        }

        player.Enqueue(samples);

        // The device is asked for the backlog rather than for what was handed over, because what matters is how far
        // behind the sound is.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(player.Buffered >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Handing_over_more_than_the_device_can_take_does_not_grow_a_queue()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        await using var player = new PulseAudioPlayer();
        await player.StartAsync(AudioFormat.Standard);

        var block = new float[AudioFormat.Standard.SampleRate * AudioFormat.Standard.Channels / 10];

        // Far more than any device can buffer. Each call either writes or throws the audio away; what must not
        // happen is the backlog growing without bound, which would leave the sound further and further behind the
        // picture with no way to catch up.
        for (var i = 0; i < 200; i++)
        {
            player.Enqueue(block);
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.True(
            player.Buffered < TimeSpan.FromSeconds(3),
            $"The queue grew to {player.Buffered}, which is far more than the device can be playing behind.");
    }

    [Fact]
    public async Task A_change_of_output_device_ends_the_capture_so_the_caller_follows_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        await using var setup = new PulseSetup();
        var sinks = await setup.ListSinksAsync();
        if (sinks.Count < 2)
        {
            Assert.Skip("This machine has only one output device, so there is nothing to switch to.");
        }

        // The test changes the default output, so it puts it back. It uses sinks that are already there rather than
        // making one: loading a module to test with would leave something behind if the test ever failed halfway.
        var original = await setup.DefaultSinkAsync();
        var other = sinks.First(name => !string.Equals(name, original, StringComparison.Ordinal));

        await using var capturer = new PulseAudioCapturer();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in capturer.CaptureAsync(stop.Token))
                {
                }

                ended.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                ended.TrySetResult(false);
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            // Longer than one interval between checks, so that the capture has had a chance to see the name it
            // started on and would have ended by now if it were going to end for no reason.
            await Task.Delay(1200, TestContext.Current.CancellationToken);
            Assert.False(ended.Task.IsCompleted, "The capture ended before anything changed.");

            await setup.SetDefaultSinkAsync(other);

            // The sequence ending is how the host is told to start a new capture, which resolves the default monitor
            // again and lands on the newly chosen device.
            Assert.True(
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
                "The capture did not notice the output device changing.");
        }
        finally
        {
            await setup.SetDefaultSinkAsync(original);
            await stop.CancelAsync();
            await running;
        }
    }

    /// <summary>
    /// Talks to the sound server directly, for the one test that needs to change which output is the default.
    /// </summary>
    /// <remarks>
    /// Nothing here loads or unloads a module. An earlier version did, to have a second output to switch to, and
    /// twice left a null sink behind on the machine when the test process died partway through.
    /// </remarks>
    private sealed class PulseSetup : IAsyncDisposable
    {
        private readonly nint _mainloop;
        private readonly nint _context;

        public PulseSetup()
        {
            _mainloop = PulseApi.MainloopNew();
            _context = PulseApi.ContextNew(PulseApi.MainloopGetApi(_mainloop), "Remote Desktop test");

            unsafe
            {
                if (PulseApi.ContextConnect(_context, null, 0, 0) != 0)
                {
                    throw new InvalidOperationException("Could not reach the sound server for the test.");
                }
            }

            for (var attempt = 0; attempt < 3000 && PulseApi.ContextGetState(_context) != PulseApi.ContextReady; attempt++)
            {
                Pump();
                Thread.Sleep(1);
            }

            if (PulseApi.ContextGetState(_context) != PulseApi.ContextReady)
            {
                throw new InvalidOperationException("The sound server did not become ready for the test.");
            }
        }

        /// <summary>Every output the machine has.</summary>
        public async Task<List<string>> ListSinksAsync()
        {
            unsafe
            {
                _ = PulseApi.ContextGetSinkInfoList(_context, Callback, 0);
            }

            await PumpForAsync(TimeSpan.FromMilliseconds(500));
            return _names;
        }

        /// <summary>The name of the output that is currently the default.</summary>
        public async Task<string> DefaultSinkAsync()
        {
            unsafe
            {
                _ = PulseApi.ContextGetSinkInfoByName(_context, "@DEFAULT_SINK@", Callback, 0);
            }

            await PumpForAsync(TimeSpan.FromMilliseconds(500));
            return _names.Count > 0 ? _names[0] : throw new InvalidOperationException("The sound server named no default output.");
        }

        public async Task SetDefaultSinkAsync(string name)
        {
            unsafe
            {
                _ = PulseApi.ContextSetDefaultSink(_context, name, 0, 0);
            }

            await PumpForAsync(TimeSpan.FromMilliseconds(300));
        }

        public ValueTask DisposeAsync()
        {
            PulseApi.ContextDisconnect(_context);
            PulseApi.ContextUnref(_context);
            PulseApi.MainloopFree(_mainloop);
            return ValueTask.CompletedTask;
        }

        private static List<string> _names = [];

        private static unsafe nint Callback =>
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, void>)&OnSink;

        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static void OnSink(nint context, nint info, int endOfList, nint userdata)
        {
            _ = context;
            _ = userdata;

            // The name is the first field of the structure, the one position in a C struct that cannot be wrong.
            if (endOfList == 0 && info != 0 && Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info)) is { } name)
            {
                _names.Add(name);
            }
        }

        private async Task PumpForAsync(TimeSpan duration)
        {
            var deadline = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < deadline)
            {
                Pump();
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }

        private void Pump() => _ = PulseApi.Iterate(_mainloop, 0, out _);
    }
}
