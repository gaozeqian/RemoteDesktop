using Xunit;

namespace RemoteDesktop.TestSupport;

/// <summary>
/// The "wait until the other end has caught up" loop, in one place. Nearly every file in the two integration suites
/// grew its own copy with a different timeout, polling interval and failure mode; a copy that returned quietly on
/// timeout made its assertions pass without the state they were about.
/// </summary>
internal static class TestWait
{
    /// <summary>Long enough for a real handshake, short enough that a deadlock fails the test rather than the run.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Waits for <paramref name="condition"/>; a timeout is an exception, never a quiet return.</summary>
    public static async Task UntilAsync(
        Func<bool> condition,
        string what,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(pollInterval ?? DefaultPollInterval, Token(cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>The same wait for a condition that has to be asked for over a connection.</summary>
    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        string what,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(pollInterval ?? DefaultPollInterval, Token(cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>For tests that assert the absence of a state: returns false instead of throwing on timeout.</summary>
    public static async Task<bool> TryUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(pollInterval ?? DefaultPollInterval, Token(cancellationToken)).ConfigureAwait(false);
        }

        return condition();
    }

    private static CancellationToken Token(CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? cancellationToken : TestContext.Current.CancellationToken;
}
