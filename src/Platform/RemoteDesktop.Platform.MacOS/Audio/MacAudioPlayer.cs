using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Audio;

/// <summary>Creates players for the default output device.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacAudioPlayerFactory : IAudioPlayerFactory
{
    public IAudioPlayer Create() => new MacAudioPlayer();
}

/// <summary>
/// Plays interleaved float PCM on the default output device through AudioQueue.
/// </summary>
/// <remarks>
/// <para>
/// The device is the system default, which AudioQueue follows if the user switches outputs — the same behaviour the
/// Windows player asks for by routing to the default stream.
/// </para>
/// <para>
/// Latency is bounded here rather than in the helper. The native ring is a full second deep so that it cannot grow
/// without limit, but a second of delay is not something a remote session can live with, so this layer discards the
/// backlog once it passes <see cref="MaxBuffered"/> and the ring stays a safety net underneath.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacAudioPlayer : IAudioPlayer
{
    /// <summary>
    /// When more than this is queued the device has fallen behind; the backlog goes, trading a click for latency.
    /// The same bound the Windows player applies, so a session sounds the same on either host.
    /// </summary>
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(200);

    private nint _player;
    private AudioFormat? _format;

    public TimeSpan Buffered
    {
        get
        {
            if (_player == 0 || _format is null)
            {
                return TimeSpan.Zero;
            }

            var samples = MacNativeInterop.rd_audio_player_buffered(_player);
            if (samples <= 0)
            {
                return TimeSpan.Zero;
            }

            // The helper counts samples, not frames, and the caller already knows the format it opened with.
            var frames = samples / _format.Channels;
            return TimeSpan.FromSeconds((double)frames / _format.SampleRate);
        }
    }

    public Task StartAsync(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (_player != 0)
        {
            throw new InvalidOperationException("The player was already started.");
        }

        var player = Open(format);
        try
        {
            Start(player);
        }
        catch
        {
            // A device that will not start is not one to keep a handle to.
            MacNativeInterop.rd_audio_player_destroy(player);
            throw;
        }

        _player = player;
        _format = format;
        return Task.CompletedTask;
    }

    public void Enqueue(ReadOnlySpan<float> samples)
    {
        if (_player == 0)
        {
            throw new InvalidOperationException("The player was not started.");
        }

        if (samples.IsEmpty)
        {
            return;
        }

        if (Buffered > MaxBuffered)
        {
            MacNativeInterop.rd_audio_player_discard(_player);
        }

        unsafe
        {
            fixed (float* samplesPointer = samples)
            {
                var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
                error[0] = 0;

                // A pointer and an explicit length: see the remark on rd_audio_player_enqueue. Passing the span
                // itself would leave the helper reading whatever followed the pointer as a sample count.
                var status = MacNativeInterop.rd_audio_player_enqueue(
                    _player, samplesPointer, (nuint)samples.Length, error, MacNativeInterop.ErrorBufferSize);

                if (status != MacNativeInterop.Ok)
                {
                    throw new InvalidOperationException(
                        MacNativeInterop.Describe(status, error, MacNativeInterop.ErrorBufferSize));
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_player != 0)
        {
            // Stops first, then releases; the helper does both.
            MacNativeInterop.rd_audio_player_destroy(_player);
            _player = 0;
            _format = null;
        }

        return ValueTask.CompletedTask;
    }

    private static unsafe nint Open(AudioFormat format)
    {
        var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
        error[0] = 0;

        var status = MacNativeInterop.rd_audio_player_create(
            format.SampleRate, format.Channels, out var player, error, MacNativeInterop.ErrorBufferSize);

        if (status != MacNativeInterop.Ok)
        {
            throw new InvalidOperationException(
                MacNativeInterop.Describe(status, error, MacNativeInterop.ErrorBufferSize));
        }

        return player;
    }

    private static unsafe void Start(nint player)
    {
        var error = stackalloc byte[MacNativeInterop.ErrorBufferSize];
        error[0] = 0;

        var status = MacNativeInterop.rd_audio_player_start(player, error, MacNativeInterop.ErrorBufferSize);
        if (status != MacNativeInterop.Ok)
        {
            throw new InvalidOperationException(
                MacNativeInterop.Describe(status, error, MacNativeInterop.ErrorBufferSize));
        }
    }
}
