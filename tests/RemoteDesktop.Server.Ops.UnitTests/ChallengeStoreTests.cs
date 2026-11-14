using RemoteDesktop.Server.Ops.Core.Security;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// The half-finished sign-ins between the password step and the TOTP code. An entry that outlives its five minutes, or
/// survives five wrong codes, is a credential that can still be guessed at — the store, not the caller, owns those
/// limits.
/// </summary>
public sealed class ChallengeStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_challenge_expires_after_five_minutes()
    {
        var clock = new TestClock(Start);
        var store = new ChallengeStore(clock);
        var id = store.Create("user-1", out var expiresAt);

        Assert.Equal(Start.AddMinutes(5), expiresAt);
        Assert.True(store.TryGet(id, out var userId, out _));
        Assert.Equal("user-1", userId);

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.False(store.TryGet(id, out _, out _));
    }

    [Fact]
    public void Five_wrong_codes_discard_the_challenge()
    {
        var store = new ChallengeStore(new TestClock(Start));
        var id = store.Create("user-1", out _);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.True(store.RecordFailure(id), $"attempt {attempt} should still leave the challenge usable");
        }

        Assert.False(store.RecordFailure(id));
        Assert.False(store.TryGet(id, out _, out _));
    }

    [Fact]
    public void A_successful_code_consumes_the_challenge()
    {
        var store = new ChallengeStore(new TestClock(Start));
        var id = store.Create("user-1", out _);

        store.Remove(id);

        Assert.False(store.TryGet(id, out _, out _));
        store.Remove(id); // consuming twice is not an error
    }

    /// <summary>
    /// Starting sign-ins is anonymous, so the store needs a ceiling that does not depend on them expiring. Past it the
    /// oldest pending challenge goes; a completed sign-in is never in this dictionary.
    /// </summary>
    [Fact]
    public void The_store_does_not_grow_past_its_cap()
    {
        var store = new ChallengeStore(new TestClock(Start));
        for (var i = 0; i < 10_001; i++)
        {
            store.Create($"user-{i}", out _);
        }

        Assert.Equal(10_000, store.Count);
    }
}
