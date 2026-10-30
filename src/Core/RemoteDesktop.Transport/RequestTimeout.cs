namespace RemoteDesktop.Transport;

/// <summary>
/// Deadline for a request that expects exactly one answer over its own channel: a folder listing, a shell to start,
/// a camera list, a transfer that is waiting to be accepted.
/// </summary>
/// <remarks>
/// <para>
/// Only the wait for that answer is bounded, never the work itself: a transfer's body, a terminal's output and a
/// camera stream stay under the caller's own token. Without this, a host that accepts the channel and then says
/// nothing leaves the file pane, the terminal window or the camera list waiting for as long as the session lasts, and
/// the only way out is closing it.
/// </para>
/// <para>
/// A deadline reads as <see cref="TimeoutException"/> carrying a sentence the caller can show; cancelling through
/// <c>cancellationToken</c> still reads as <see cref="OperationCanceledException"/>, so "the user closed the window"
/// and "the host went quiet" stay distinguishable.
/// </para>
/// </remarks>
public static class RequestTimeout
{
    /// <summary>How long a one-shot request waits for its answer.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(30);

    /// <summary>Runs <paramref name="request"/> under a deadline.</summary>
    /// <param name="request">The request; it must use the token it is given, which is the only way the deadline stops it.</param>
    /// <param name="cancellationToken">The caller's own cancellation, which is passed through unchanged.</param>
    /// <param name="timeout">The deadline; <see cref="Default"/> when omitted.</param>
    /// <exception cref="TimeoutException">Nothing arrived before the deadline.</exception>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> request,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(timeout ?? Default);
        try
        {
            return await request(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the deadline fired: the peer is silent, not the person at this computer.
            throw new TimeoutException("The host did not answer in time.");
        }
    }

    /// <inheritdoc cref="RunAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken, TimeSpan?)"/>
    public static Task RunAsync(Func<CancellationToken, Task> request, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RunAsync<object?>(
            async token =>
            {
                await request(token).ConfigureAwait(false);
                return null;
            },
            cancellationToken,
            timeout);
    }
}
