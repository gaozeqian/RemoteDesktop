namespace RemoteDesktop.Media;

/// <summary>
/// Additive-increase / multiplicative-decrease on the encoder bitrate. Congestion shows up as frames whose send takes
/// longer than the frame interval (the transport's send buffer is full); a calm period lets the rate climb back.
/// </summary>
public sealed class BitrateController
{
    private static readonly TimeSpan DecreaseCooldown = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IncreaseAfter = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _frameInterval;
    private TimeSpan _lastChange;
    private TimeSpan _lastCongestion;

    public BitrateController(int initialKbps, int minKbps, int maxKbps, int frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKbps, minKbps);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameRate, 1);

        MinKbps = minKbps;
        MaxKbps = maxKbps;
        CurrentKbps = Math.Clamp(initialKbps, minKbps, maxKbps);
        _frameInterval = TimeSpan.FromSeconds(1.0 / frameRate);
    }

    public int MinKbps { get; }

    public int MaxKbps { get; }

    public int CurrentKbps { get; private set; }

    /// <summary>Reports how long sending one frame took; returns the new bitrate when it changed.</summary>
    /// <param name="now">Monotonic time.</param>
    public int? OnFrameSent(TimeSpan sendDuration, TimeSpan now)
    {
        if (sendDuration > _frameInterval * 2)
        {
            _lastCongestion = now;
            if (now - _lastChange >= DecreaseCooldown)
            {
                return Change((int)(CurrentKbps * 0.75), now);
            }

            return null;
        }

        if (now - _lastCongestion >= IncreaseAfter && now - _lastChange >= IncreaseAfter)
        {
            return Change((int)(CurrentKbps * 1.15) + 100, now);
        }

        return null;
    }

    private int? Change(int kbps, TimeSpan now)
    {
        kbps = Math.Clamp(kbps, MinKbps, MaxKbps);
        if (kbps == CurrentKbps)
        {
            return null;
        }

        CurrentKbps = kbps;
        _lastChange = now;
        return kbps;
    }
}
