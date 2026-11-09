using RemoteDesktop.Server.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

public sealed class SessionDirectoryTests
{
    [Fact]
    public void Only_the_assigned_device_and_role_may_report_a_session()
    {
        var sessions = new SessionDirectory(TimeProvider.System);
        var correlation = Guid.NewGuid();
        sessions.Register(correlation, controllerDeviceId: 100000001, hostDeviceId: 100000002);

        Assert.True(sessions.IsParticipant(correlation, 100000001, "controller"));
        Assert.True(sessions.IsParticipant(correlation, 100000002, "host"));
        Assert.False(sessions.IsParticipant(correlation, 100000003, "controller"));
        Assert.False(sessions.IsParticipant(correlation, 100000001, "host"));
        Assert.False(sessions.IsParticipant(correlation, 100000002, "controller"));
        Assert.False(sessions.IsParticipant(Guid.NewGuid(), 100000001, "controller"));
    }
}
