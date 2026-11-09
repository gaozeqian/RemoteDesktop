using RemoteDesktop.Platform.MacOS.Input;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

public sealed class InputSessionArbitratorTests
{
    [Fact]
    public void Another_session_cannot_release_or_change_the_active_sessions_input_state()
    {
        var sessions = new InputSessionArbitrator();

        Assert.True(sessions.TryInject("a"));
        Assert.False(sessions.TryInject("b"));
        sessions.Release("b");
        Assert.True(sessions.TryInject("a"));

        sessions.Release("a");
        Assert.True(sessions.TryInject("b"));
    }
}
