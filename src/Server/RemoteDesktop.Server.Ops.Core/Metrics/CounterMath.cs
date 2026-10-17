namespace RemoteDesktop.Server.Ops.Core;

/// <summary>One reading of a counter: when, and the cumulative value.</summary>
public readonly record struct CounterPoint(long Timestamp, double Value);

/// <summary>
/// Turns cumulative counters into rates, which is the only form a chart or a threshold can use.
/// </summary>
/// <remarks>
/// The interesting case is a reset: a counter that goes down did not send negative traffic, it restarted. Reading the
/// drop as a delta would draw a spike downward followed by one upward, and an alert rule would fire on a process
/// restart. A reset therefore counts as "the new value arrived since the last reading", which is the standard treatment
/// and is why the raw value is what gets stored rather than a rate computed at write time — the latter bakes in a wrong
/// answer that no later query can undo.
/// </remarks>
public static class CounterMath
{
    /// <summary>
    /// Total increase over <paramref name="points"/>, which must be ordered oldest first. Resets are folded in.
    /// </summary>
    public static double Increase(IReadOnlyList<CounterPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var total = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            var delta = points[index].Value - points[index - 1].Value;
            total += delta >= 0 ? delta : points[index].Value;
        }

        return total;
    }

    /// <summary>
    /// Per-second rates between consecutive readings, stamped with the later reading's time. Points with a
    /// non-positive interval are skipped: their rate is undefined, not zero.
    /// </summary>
    public static IReadOnlyList<CounterPoint> Rates(IReadOnlyList<CounterPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var rates = new List<CounterPoint>(Math.Max(0, points.Count - 1));
        for (var index = 1; index < points.Count; index++)
        {
            var seconds = (points[index].Timestamp - points[index - 1].Timestamp) / 1000d;
            if (seconds <= 0)
            {
                continue;
            }

            var delta = points[index].Value - points[index - 1].Value;
            var increase = delta >= 0 ? delta : points[index].Value;
            rates.Add(new CounterPoint(points[index].Timestamp, increase / seconds));
        }

        return rates;
    }

    /// <summary>Average per-second rate over the whole window, or zero when there is nothing to divide by.</summary>
    public static double AverageRate(IReadOnlyList<CounterPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 2)
        {
            return 0;
        }

        var seconds = (points[^1].Timestamp - points[0].Timestamp) / 1000d;
        return seconds <= 0 ? 0 : Increase(points) / seconds;
    }
}
