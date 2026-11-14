using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Audio;

namespace RemoteDesktop.Platform.Linux.Audio;

/// <summary>
/// Plays audio through PulseAudio, dropping what it cannot keep up with.
/// </summary>
/// <remarks>
/// <para>
/// This is the controller's side of the audio channel: whatever arrives from the far machine is played here. The
/// picture and the sound have to stay together, so a device that has fallen behind is caught up by throwing away
/// what is queued rather than by playing it late — a listener notices a click far less than they notice commentary
/// arriving a second after the thing it describes.
/// </para>
/// <para>
/// The same choice the Windows player makes, and for the same reason.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class PulseAudioPlayer : IAudioPlayer
{
    /// <summary>Queued audio beyond this is thrown away rather than played late.</summary>
    private static readonly TimeSpan MaximumBacklog = TimeSpan.FromMilliseconds(200);

    private nint _mainloop;
    private nint _context;
    private nint _stream;
    private int _channels;
    private bool _disposed;

    public TimeSpan Buffered
    {
        get
        {
            if (_stream == 0 || Pulse.StreamGetLatency(_stream, out var microseconds, out _) < 0)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromMicroseconds(microseconds);
        }
    }

    public async Task StartAsync(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _channels = format.Channels;
        Connect(format);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Queues interleaved float samples.
    /// </summary>
    /// <remarks>
    /// Deliberately synchronous and non-blocking: it is called from the loop that is receiving audio off the network,
    /// and waiting there would make the sound the reason the picture stalls.
    /// </remarks>
    public void Enqueue(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_stream == 0 || samples.IsEmpty)
        {
            return;
        }

        // Give the server a chance to take what was written last time before deciding how much room there is.
        _ = Pulse.Iterate(_mainloop, 0, out _);

        if (Buffered > MaximumBacklog)
        {
            // Chosen over playing it late, which would leave the sound permanently behind the picture and never
            // catch up on its own.
            _ = Pulse.StreamFlush(_stream);
        }

        var bytes = (nuint)samples.Length * sizeof(float);
        if (Pulse.StreamWritableSize(_stream) < bytes)
        {
            // The device is full even after flushing: drop this rather than grow a queue that would only make the
            // delay worse.
            return;
        }

        unsafe
        {
            fixed (float* data = samples)
            {
                _ = Pulse.StreamWrite(_stream, (nint)data, bytes, 0, 0, 0);
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

        return ValueTask.CompletedTask;
    }

    private void Connect(AudioFormat format)
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
                Rate = (uint)format.SampleRate,
                Channels = (byte)format.Channels,
            };

            // A small target keeps the delay down; the backlog check above is what stops it growing without bound.
            var attributes = new Pulse.BufferAttr
            {
                MaxLength = (uint)format.SampleRate * (uint)format.Channels * sizeof(float) / 2,
                TargetLength = (uint)format.SampleRate * (uint)format.Channels * sizeof(float) / 10,
                PreBuffering = 0,
                MinimumRequest = 0,
                FragmentSize = 0,
            };

            _stream = Pulse.StreamNew(_context, "Remote Desktop playback", &spec, 0);
            if (_stream == 0)
            {
                throw new InvalidOperationException("Could not create a PulseAudio playback stream.");
            }

            if (Pulse.StreamConnectPlayback(_stream, null, &attributes, 0, 0, 0) < 0)
            {
                throw new InvalidOperationException($"Could not start playing: {Pulse.Describe(Pulse.ContextErrno(_context))}.");
            }

            PulseWait.ForStream(_mainloop, _context, _stream, "playback");
        }
    }
}
