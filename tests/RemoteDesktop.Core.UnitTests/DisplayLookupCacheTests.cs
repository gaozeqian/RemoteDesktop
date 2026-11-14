using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Platform.Shared;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The display list three input injectors used to cache by hand. The lifetime differs per platform, the loop that
/// implements it does not, and these are the two facts the platform call sites rely on.
/// </summary>
public sealed class DisplayLookupCacheTests
{
    private static readonly DisplayInfo Primary = new(
        "display-1",
        "Primary",
        new PixelRect(0, 0, 1920, 1080),
        1,
        IsPrimary: true);

    [Fact]
    public void Displays_are_enumerated_once_per_lifetime()
    {
        var clock = new TestClock();
        var enumerator = new CountingEnumerator([Primary]);
        var cache = new DisplayLookupCache(enumerator, TimeSpan.FromSeconds(2), clock);

        Assert.Same(Primary, Assert.Single(cache.GetDisplays()));
        cache.GetDisplays();
        Assert.Equal(1, enumerator.Calls);

        clock.Advance(TimeSpan.FromSeconds(2.001));

        Assert.Same(Primary, Assert.Single(cache.GetDisplays()));
        Assert.Equal(2, enumerator.Calls);
    }

    [Fact]
    public void An_empty_list_is_retried_before_the_lifetime_ends()
    {
        // A display that is momentarily absent (a monitor waking up, a compositor restarting) must not be cached as
        // "no displays" for the whole lifetime: the injector would refuse input it could otherwise deliver.
        var clock = new TestClock();
        var enumerator = new CountingEnumerator([], [Primary]);
        var cache = new DisplayLookupCache(enumerator, TimeSpan.FromMinutes(1), clock);

        Assert.Empty(cache.GetDisplays());
        Assert.Same(Primary, Assert.Single(cache.GetDisplays()));
        Assert.Equal(2, enumerator.Calls);
    }

    private sealed class CountingEnumerator(params IReadOnlyList<DisplayInfo>[] results) : IDisplayEnumerator
    {
        private int _next;

        public int Calls { get; private set; }

        public IReadOnlyList<DisplayInfo> GetDisplays()
        {
            Calls++;
            var result = results[Math.Min(_next, results.Length - 1)];
            _next++;
            return result;
        }
    }
}
