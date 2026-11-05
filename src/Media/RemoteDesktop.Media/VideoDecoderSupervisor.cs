using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Media;

/// <summary>
/// Owns the decoder of one received video stream and decides when it has lost sync and must be replaced. Both
/// controller-side viewers (desktop and camera) feed a decoder that can fall silent: it keeps taking non-key frames
/// and never shows a picture again. This is the one copy of that judgement, so the two viewers cannot drift apart.
/// </summary>
/// <remarks>
/// Not thread-safe, with one exception: the time of the last picture may be read from another thread (a watchdog).
/// Everything else belongs to the receive loop, which is the decoder's only user.
/// </remarks>
public sealed class VideoDecoderSupervisor : IDisposable
{
    /// <summary>
    /// The largest stream this will set up buffers for. Every stream info allocates four bytes per pixel, and the size
    /// is whatever the peer says it is, so "the host is trusted to be well-formed" does not cover this: a host that is
    /// not really a host, or one that is confused, would otherwise have this window allocate a gigabyte on request,
    /// over and over. 8K UHD is the largest display there is, so nothing real is refused.
    /// </summary>
    public const int MaxDimension = 8192;

    /// <summary>The most pixels a stream may have; see <see cref="MaxDimension"/>.</summary>
    public const long MaxPixels = 7680L * 4320;

    /// <summary>
    /// Non-key frames in a row that must come back without a picture before the decoder is judged out of sync (on top
    /// of <see cref="StallTimeout"/>). One quiet answer is ordinary; a run of them while the host keeps sending is not.
    /// </summary>
    public const int SilentFramesBeforeRebuild = 3;

    /// <summary>No picture for this long and a key frame is asked for: the stream may simply have lost sync.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(2);

    private readonly VideoCodecSelector _codecs;
    private readonly TimeProvider _clock;
    private readonly Action<Exception> _onRebuildFailed;
    private int _width;
    private int _height;
    private int _silentFrames;
    private long _lastPictureAt;

    /// <param name="codecs">Where decoders come from.</param>
    /// <param name="clock">
    /// The monotonic clock the stall time is measured with (<see cref="TimeProvider.System"/> in production). The
    /// viewers' own injectable clock is deliberately not this one: it drives their watchdog tick and latency figures.
    /// </param>
    /// <param name="onRebuildFailed">Told why a rebuild could not create the new decoder; the old one stays in place.</param>
    public VideoDecoderSupervisor(VideoCodecSelector codecs, TimeProvider clock, Action<Exception> onRebuildFailed)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(onRebuildFailed);
        _codecs = codecs;
        _clock = clock;
        _onRebuildFailed = onRebuildFailed;
        _lastPictureAt = clock.GetTimestamp();
    }

    /// <summary>The current decoder, or <c>null</c> until the first <see cref="Start"/>.</summary>
    public IVideoDecoder? Decoder { get; private set; }

    /// <summary>Whether a stream of this size is one this will set up buffers for.</summary>
    public static bool IsAcceptableSize(int width, int height) =>
        width >= 2 && height >= 2 && width <= MaxDimension && height <= MaxDimension && (long)width * height <= MaxPixels;

    /// <summary>
    /// Begins a new stream: every stream info starts a new encoder on the host (the stream is reconfigured, or the host
    /// swapped its agent and continues with a fresh encoder), so a decoder from the old stream must not be fed the new
    /// one, even at the same resolution.
    /// </summary>
    /// <exception cref="InvalidDataException">The size is not acceptable; nothing was changed.</exception>
    public void Start(int width, int height)
    {
        if (!IsAcceptableSize(width, height))
        {
            throw new InvalidDataException($"Invalid stream size {width}x{height}.");
        }

        Decoder?.Dispose();
        Decoder = _codecs.CreateDecoder(VideoCodec.H264, width, height);
        _width = width;
        _height = height;
        _silentFrames = 0;
        Volatile.Write(ref _lastPictureAt, _clock.GetTimestamp());
    }

    /// <summary>A picture came out of the decoder: it is alive, so both the silent run and the quiet time start over.</summary>
    public void PictureShown()
    {
        _silentFrames = 0;
        Volatile.Write(ref _lastPictureAt, _clock.GetTimestamp());
    }

    /// <summary>
    /// A frame was decoded without a picture; counts it. Returns whether the decoder is now judged out of sync, in which case the
    /// caller should say so, call <see cref="Rebuild"/> and ask the host for a key frame.
    /// </summary>
    /// <remarks>
    /// Only a run of non-key frames counts. A decoder may legitimately answer nothing for a unit that carries just
    /// parameter sets, or while it wants more input; a rebuild on the strength of one of those would throw away the
    /// SPS/PPS it just learned and leave the next picture undecodable.
    /// </remarks>
    public bool RecordSilentFrame(bool frameWasKeyFrame) =>
        !frameWasKeyFrame
        && ++_silentFrames >= SilentFramesBeforeRebuild
        && QuietFor(_clock.GetTimestamp()) >= StallTimeout;

    /// <summary>How long ago the last picture was shown (or the stream began), measured up to <paramref name="now"/>.</summary>
    /// <param name="now">A timestamp from the clock this was created with.</param>
    public TimeSpan QuietFor(long now) => _clock.GetElapsedTime(Volatile.Read(ref _lastPictureAt), now);

    /// <summary>
    /// Replaces the decoder with a fresh one for the current stream. For the receive loop (the decoder's only owner) to
    /// call when <see cref="RecordSilentFrame"/> says so.
    /// </summary>
    public void Rebuild()
    {
        // Whatever happens, start counting again: a rebuild that failed must not be retried on the very next frame.
        _silentFrames = 0;
        Volatile.Write(ref _lastPictureAt, _clock.GetTimestamp());

        if (_width == 0)
        {
            return;
        }

        // The new decoder comes first. Creating it can fail in backend-specific ways (no hardware decoder left, a
        // device that was lost); the old one is then still a decoder, and the caller's key frame request gives it a
        // chance, where a disposed one in Decoder would end the whole viewer on the next frame.
        IVideoDecoder fresh;
        try
        {
            fresh = _codecs.CreateDecoder(VideoCodec.H264, _width, _height);
        }
#pragma warning disable CA1031 // Any backend failure leaves the old decoder in place.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _onRebuildFailed(ex);
            return;
        }

        var old = Decoder;
        Decoder = fresh;
        old?.Dispose();
    }

    public void Dispose() => Decoder?.Dispose();
}
