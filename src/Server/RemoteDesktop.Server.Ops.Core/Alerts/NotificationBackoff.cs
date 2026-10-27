namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// How long a failed webhook delivery waits before the next attempt.
/// </summary>
/// <remarks>
/// The console evaluates rules every few seconds; retrying a webhook at that rate would turn a receiver that is down
/// for a minute into hundreds of requests. The delay doubles from one minute to half an hour and stops after a small
/// number of attempts, which is enough to ride out a restart or a short network fault without becoming a permanent
/// load on a webhook that is simply wrong.
/// </remarks>
public static class NotificationBackoff
{
    /// <summary>How many delivery attempts a transition gets before it is left in the table as failed.</summary>
    public const int MaxAttempts = 5;

    /// <summary>The delay before attempt number <paramref name="attempts"/> + 1.</summary>
    public static TimeSpan Delay(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 10);
        var seconds = Math.Min(TimeSpan.FromMinutes(30).TotalSeconds, 60 * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>True when a failed attempt is old enough to try again.</summary>
    public static bool IsDue(long? lastAttemptAtUnixMilliseconds, int attempts, DateTimeOffset now)
    {
        if (lastAttemptAtUnixMilliseconds is not { } last)
        {
            return true;
        }

        return now >= DateTimeOffset.FromUnixTimeMilliseconds(last).Add(Delay(attempts));
    }
}
