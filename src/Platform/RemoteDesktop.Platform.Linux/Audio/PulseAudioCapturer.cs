using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Audio;

namespace RemoteDesktop.Platform.Linux.Audio;

/// <summary>
/// Captures what the machine is playing, by recording from the output device's monitor source.
/// </summary>
/// <remarks>
/// <para>
/// This is the Linux equivalent of a WASAPI loopback capture, and it is what makes the interface's sentence about
/// silence something the implementation has to arrange rather than something it gets for free. A loopback device on
/// Windows only delivers data while something is rendering; a monitor source delivers a continuous stream of digital
/// silence whether anything is playing or not. Passing that on unchanged would have the host encode and send silence
/// for the whole time a controller is connected — which is the entire session, on a machine nobody is using.
/// </para>
/// <para>
/// So silence is dropped here. The reader still consumes it — the samples have to be taken off the stream either way
/// — but they do not become a chunk.
/// </para>
/// <para>
/// The stream is asked for the rate and layout the pipeline carries rather than for the device's own, so the server
/// does the resampling and remixing. The interface says implementations are the ones to convert; letting the sound
/// server do it is how this one does.
/// </para>
/// <para>
/// Following the user's headphones is done by asking the server what its default output is called, every so often,
/// rather than by subscribing to its events. Subscribing would be the obvious way and was the first attempt, but it
/// needs the event-category numbers — which are not in a header on this machine and which, guessed wrong, produce a
/// capture that ends the moment anything at all happens on the sound server, including the capture's own creation.
/// Asking for the name needs no such number, and the answer is the thing being watched for anyway.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class PulseAudioCapturer : IAudioCapturer
{
    /// <summary>The default output device's monitor, spelled the way PulseAudio resolves it.</summary>
    private const string DefaultMonitor = "@DEFAULT_MONITOR@";

    /// <summary>How long to wait before looking again when the stream has nothing to give.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>Below this, a sample is silence. Float audio has no meaningful noise floor of its own here.</summary>
    private const float SilenceThreshold = 1e-6f;

    /// <summary>How often to ask whether the default output is still the one this capture is watching.</summary>
    private static readonly TimeSpan SinkCheckInterval = TimeSpan.FromMilliseconds(500);

    private nint _mainloop;
    private nint _context;
    private nint _stream;
    private GCHandle _self;
    private string? _startedOnSink;
    private string? _reportedSink;
    private bool _asking;
    private DateTime _nextSinkCheck;
    private bool _disposed;

    public AudioFormat Format => AudioFormat.Standard;

    public async IAsyncEnumerable<ReadOnlyMemory<float>> CaptureAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Connect();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Iterate())
            {
                yield break;
            }

            // The output device changed, so this monitor is the wrong one now. Ending the sequence is how the caller
            // is told: it starts a new capture, which resolves the default monitor again and lands on the new device.
            if (DefaultOutputChanged())
            {
                yield break;
            }

            // Checking on a timer rather than on every pass: this is a round trip to the sound server, and the answer
            // only changes when a person changes it.
            if (DateTime.UtcNow >= _nextSinkCheck)
            {
                _nextSinkCheck = DateTime.UtcNow + SinkCheckInterval;
                AskForDefaultSink();
            }

            if (Pulse.StreamReadableSize(_stream) == 0)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (ReadChunk(out var chunk) && !IsSilent(chunk.Span))
            {
                yield return chunk;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        Disconnect();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Records what the default output is called. Called from inside <see cref="Iterate"/>, on whichever thread is
    /// running the capture, so there is no second thread to synchronise with.
    /// </summary>
    /// <remarks>
    /// The name is the first field of the structure the server fills in, which is the one position in a C struct that
    /// cannot be wrong: the pointer sits at offset zero by definition.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnSinkInfo(nint context, nint info, int endOfList, nint userdata)
    {
        _ = context;

        if (endOfList != 0 || info == 0 || GCHandle.FromIntPtr(userdata).Target is not PulseAudioCapturer capturer)
        {
            return;
        }

        capturer._asking = false;
        capturer._reportedSink = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info));
    }

    /// <summary>Asks the server what its default output is called, unless a question is already in flight.</summary>
    private unsafe void AskForDefaultSink()
    {
        if (_asking)
        {
            return;
        }

        _asking = true;
        _ = Pulse.ContextGetSinkInfoByName(
            _context,
            "@DEFAULT_SINK@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, void>)&OnSinkInfo,
            GCHandle.ToIntPtr(_self));
    }

    /// <summary>Whether the default output is now something other than the one this capture started on.</summary>
    private bool DefaultOutputChanged()
    {
        // Nothing has answered yet: treating that as a change would end the capture before it had begun.
        if (_reportedSink is null)
        {
            return false;
        }

        if (_startedOnSink is null)
        {
            _startedOnSink = _reportedSink;
            return false;
        }

        return !string.Equals(_startedOnSink, _reportedSink, StringComparison.Ordinal);
    }

    private void Connect()
    {
        _mainloop = Pulse.MainloopNew();
        if (_mainloop == 0)
        {
            throw new InvalidOperationException("Could not create a PulseAudio main loop.");
        }

        _context = Pulse.ContextNew(Pulse.MainloopGetApi(_mainloop), "Remote Desktop");
        if (_context == 0)
        {
            throw new InvalidOperationException("Could not create a PulseAudio context.");
        }

        _self = GCHandle.Alloc(this);

        unsafe
        {
            if (Pulse.ContextConnect(_context, null, 0, 0) != 0)
            {
                throw new InvalidOperationException($"Could not reach PulseAudio: {Pulse.Describe(Pulse.ContextErrno(_context))}.");
            }

            PulseWait.ForContext(_mainloop, _context);

            var spec = new Pulse.SampleSpec
            {
                Format = Pulse.SampleFormatFloat32Le,
                Rate = (uint)AudioFormat.Standard.SampleRate,
                Channels = (byte)AudioFormat.Standard.Channels,
            };

            if (Pulse.FrameSize(&spec) != (nuint)(AudioFormat.Standard.Channels * sizeof(float)))
            {
                throw new InvalidOperationException(
                    "PulseAudio does not agree that this sample format is 32-bit float; the capture would be noise.");
            }

            _stream = Pulse.StreamNew(_context, "Remote Desktop capture", &spec, 0);
            if (_stream == 0)
            {
                throw new InvalidOperationException("Could not create a PulseAudio capture stream.");
            }

            if (Pulse.StreamConnectRecord(_stream, DefaultMonitor, null, 0) < 0)
            {
                throw new InvalidOperationException($"Could not start capturing: {Pulse.Describe(Pulse.ContextErrno(_context))}.");
            }

            PulseWait.ForStream(_mainloop, _context, _stream, "capture");
        }
    }

    private bool Iterate()
    {
        // Non-blocking: whatever has arrived is processed, and the caller decides how long to wait before asking
        // again. Nothing below this ever blocks on the server.
        if (Pulse.Iterate(_mainloop, 0, out _) < 0)
        {
            return false;
        }

        var state = Pulse.StreamGetState(_stream);
        return state is not (Pulse.StreamFailed or Pulse.StreamTerminated);
    }

    /// <summary>Takes one chunk off the stream, or reports that there was nothing but a gap in it.</summary>
    private unsafe bool ReadChunk(out ReadOnlyMemory<float> chunk)
    {
        chunk = default;

        if (Pulse.StreamPeek(_stream, out var data, out var bytes) < 0)
        {
            return false;
        }

        try
        {
            // A null pointer with a non-zero length is a gap — the server telling us time passed with no samples.
            // It is silence for our purposes, and dropping it is all there is to do with it.
            if (data == 0 || bytes == 0)
            {
                return false;
            }

            var samples = (int)(bytes / sizeof(float));
            var copy = new float[samples];
            new ReadOnlySpan<float>((void*)data, samples).CopyTo(copy);
            chunk = copy;
            return true;
        }
        finally
        {
            _ = Pulse.StreamDrop(_stream);
        }
    }

    /// <summary>
    /// Whether a chunk is entirely silence.
    /// </summary>
    /// <remarks>
    /// The interface promises that nothing arrives while the machine is playing nothing, and on Windows that is what
    /// the loopback device does by itself. Here it has to be arranged, or a controller left connected to an idle
    /// machine would receive a continuous Opus stream of nothing for as long as it stayed connected.
    /// </remarks>
    internal static bool IsSilent(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            if (MathF.Abs(sample) > SilenceThreshold)
            {
                return false;
            }
        }

        return true;
    }


    private void Disconnect()
    {
        if (_stream != 0)
        {
            _ = Pulse.StreamDisconnect(_stream);
            Pulse.StreamUnref(_stream);
            _stream = 0;
        }

        if (_context != 0)
        {
            Pulse.ContextDisconnect(_context);
            Pulse.ContextUnref(_context);
            _context = 0;
        }

        if (_mainloop != 0)
        {
            Pulse.MainloopFree(_mainloop);
            _mainloop = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
