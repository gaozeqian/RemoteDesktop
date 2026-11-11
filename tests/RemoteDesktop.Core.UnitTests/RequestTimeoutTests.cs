using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// A request that expects exactly one answer must not wait for it forever: a host that accepts the channel and then
/// says nothing used to leave the file pane, the terminal window and the camera list waiting for the whole session.
/// </summary>
public sealed class RequestTimeoutTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task An_answer_that_arrives_in_time_is_returned()
    {
        var answer = await RequestTimeout.RunAsync(_ => Task.FromResult(42), TestContext.Current.CancellationToken, Short);

        Assert.Equal(42, answer);
    }

    [Fact]
    public async Task A_host_that_never_answers_ends_the_request_with_a_deadline()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            RequestTimeout.RunAsync(Silence, TestContext.Current.CancellationToken, Short));

        Assert.Contains("did not answer", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>"The person closed the window" and "the host went quiet" must stay distinguishable.</summary>
    [Fact]
    public async Task A_caller_that_gives_up_is_not_reported_as_a_deadline()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RequestTimeout.RunAsync(Silence, cancel.Token, Short));
    }

    // Only the deadline can end this one: it honours the token it is given, so nothing else stops it.
    private static async Task<int> Silence(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}
