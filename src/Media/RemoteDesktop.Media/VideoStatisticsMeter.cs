using System.Diagnostics;

namespace RemoteDesktop.Media;

/// <summary>One statistics window, in the units the viewers expose.</summary>
public readonly record struct VideoStatisticsSnapshot(
    double FramesPerSecond,
    double Kbps,
    double DecodeMilliseconds,
    double? LatencyMilliseconds);

/// <summary>
/// Accumulates frames, bytes and decode time over a one-second window. Both viewers had this arithmetic inline, with
/// the same divide-by-zero guard written twice.
/// </summary>
public sealed class VideoStatisticsMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _windowStart;
    private int _frames;
    private long _bytes;
    private double _decodeMilliseconds;
    private double? _latencyMilliseconds;

    /// <summary>Adds one decoded picture; <paramref name="latencyMilliseconds"/> is the newest measured latency.</summary>
    public void Add(long bytes, TimeSpan decodeTime, double? latencyMilliseconds)
    {
        _bytes += bytes;
        _decodeMilliseconds += decodeTime.TotalMilliseconds;
        _frames++;
        _latencyMilliseconds = latencyMilliseconds;
    }

    /// <summary>Returns the window when it is full, then starts a new one.</summary>
    public bool TryTake(out VideoStatisticsSnapshot statistics)
    {
        var elapsed = _clock.Elapsed - _windowStart;
        if (elapsed < Window)
        {
            statistics = default;
            return false;
        }

        statistics = new VideoStatisticsSnapshot(
            _frames / elapsed.TotalSeconds,
            _bytes * 8 / elapsed.TotalSeconds / 1000,
            _frames == 0 ? 0 : _decodeMilliseconds / _frames,
            _latencyMilliseconds);

        _windowStart = _clock.Elapsed;
        _frames = 0;
        _bytes = 0;
        _decodeMilliseconds = 0;
        return true;
    }
}
