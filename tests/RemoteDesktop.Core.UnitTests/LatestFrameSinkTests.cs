using RemoteDesktop.Platform.Shared;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The one-frame sink the macOS screen and camera deliveries share. The rules are: the newest frame wins, the
/// replaced frame is released immediately, a timeout is false rather than an exception, and a completion ends the
/// consumer's loop only after the last frame was taken.
/// </summary>
public sealed class LatestFrameSinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_new_frame_replaces_the_waiting_one_and_disposes_it()
    {
        using var sink = new LatestFrameSink<Item>();
        var first = new Item();
        var second = new Item();

        Assert.True(sink.TryWrite(first));
        Assert.True(sink.TryWrite(second));

        Assert.True(first.Disposed, "The replaced frame went back to the pool only when the sink was disposed.");
        Assert.True(sink.TryTake(out var taken));
        Assert.Same(second, taken);
        Assert.False(sink.TryTake(out _));
    }

    [Fact]
    public async Task A_wait_that_gets_no_frame_reports_a_timeout_without_throwing()
    {
        using var sink = new LatestFrameSink<Item>();

        Assert.False(await sink.WaitAsync(TimeSpan.FromMilliseconds(50), Ct));
    }

    [Fact]
    public async Task A_wait_wakes_when_a_frame_arrives()
    {
        using var sink = new LatestFrameSink<Item>();
        var waiting = sink.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var item = new Item();
        Assert.True(sink.TryWrite(item));

        Assert.True(await waiting);
        Assert.True(sink.TryTake(out var taken));
        Assert.Same(item, taken);
    }

    [Fact]
    public async Task Completion_lets_the_last_frame_through_before_ending_the_loop()
    {
        using var sink = new LatestFrameSink<Item>();
        var item = new Item();
        Assert.True(sink.TryWrite(item));
        sink.Complete();

        Assert.True(await sink.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.True(sink.TryTake(out var taken));
        Assert.Same(item, taken);

        // The next wait must report the end instead of blocking until the timeout.
        Assert.False(await sink.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task A_completion_error_is_thrown_after_the_last_frame()
    {
        using var sink = new LatestFrameSink<Item>();
        Assert.True(sink.TryWrite(new Item()));
        sink.Complete(new InvalidOperationException("the source failed"));

        Assert.True(await sink.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.True(sink.TryTake(out _));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("the source failed", error.Message);
    }

    [Fact]
    public async Task Clear_drops_the_waiting_frame_and_its_wake_up()
    {
        using var sink = new LatestFrameSink<Item>();
        var item = new Item();
        Assert.True(sink.TryWrite(item));

        sink.Clear();

        Assert.True(item.Disposed);
        Assert.False(sink.TryTake(out _));

        // The wake-up the write left behind must be gone too, or the next wait would report a frame Clear released.
        Assert.False(await sink.WaitAsync(TimeSpan.FromMilliseconds(50), Ct));
    }

    [Fact]
    public void Disposing_releases_the_waiting_frame()
    {
        var sink = new LatestFrameSink<Item>();
        var item = new Item();
        Assert.True(sink.TryWrite(item));

        sink.Dispose();

        Assert.True(item.Disposed);
        Assert.False(sink.TryWrite(new Item()));
    }

    private sealed class Item : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
