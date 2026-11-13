using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.TestSupport;

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

    /// <summary>
    /// Registration is on the connection path, so one registration must not walk a table whose size is the number of
    /// sessions accumulated over a week. Every registration sweeps one fixed batch instead.
    /// </summary>
    [Fact]
    public void One_registration_sweeps_a_bounded_number_of_expired_sessions()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var sessions = new SessionDirectory(clock);
        var ids = Enumerable.Range(0, 600).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            sessions.Register(id, controllerDeviceId: 1, hostDeviceId: 2);
        }

        clock.Advance(TimeSpan.FromDays(8));
        sessions.Register(Guid.NewGuid(), controllerDeviceId: 3, hostDeviceId: 4);

        Assert.Equal(600 - 256, ids.Count(sessions.Contains));
    }

    [Fact]
    public void Repeated_registrations_eventually_remove_every_expired_session()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var sessions = new SessionDirectory(clock);
        var ids = Enumerable.Range(0, 600).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            sessions.Register(id, controllerDeviceId: 1, hostDeviceId: 2);
        }

        clock.Advance(TimeSpan.FromDays(8));
        for (var pass = 0; pass < 4; pass++)
        {
            sessions.Register(Guid.NewGuid(), controllerDeviceId: 3, hostDeviceId: 4);
        }

        Assert.DoesNotContain(ids, sessions.Contains);
    }
}
