using RemoteDesktop.Application.Sessions;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>The gate that keeps a flood of half-open connections from growing the host service without bound.</summary>
public sealed class ConnectionAdmissionTests
{
    [Fact]
    public void Only_the_configured_number_of_handshakes_run_at_once()
    {
        using var admission = new ConnectionAdmission(2);

        Assert.True(admission.TryEnter());
        Assert.True(admission.TryEnter());
        Assert.Equal(2, admission.InFlight);
        Assert.False(admission.TryEnter()); // the third connection is dropped by the caller

        admission.RecordRefusal();
        Assert.Equal(1, admission.Refused);

        admission.Release();
        Assert.Equal(1, admission.InFlight);
        Assert.True(admission.TryEnter());
    }

    [Fact]
    public void A_finished_handshake_frees_its_slot_for_the_next_connection()
    {
        using var admission = new ConnectionAdmission(1);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(admission.TryEnter());
            admission.Release();
        }

        Assert.Equal(0, admission.InFlight);
        Assert.Equal(0, admission.Refused);
    }

    [Fact]
    public void The_default_limit_leaves_room_for_the_usual_number_of_connections()
    {
        using var admission = new ConnectionAdmission();

        Assert.Equal(ConnectionAdmission.DefaultLimit, admission.Limit);
        Assert.True(admission.Limit >= 16); // a household with a few devices must never hit it
    }

    [Fact]
    public void Releasing_more_than_entered_does_not_corrupt_the_count()
    {
        using var admission = new ConnectionAdmission(1);

        admission.Release(); // a caller that made a mistake
        Assert.Equal(0, admission.InFlight);
        Assert.True(admission.TryEnter());
        Assert.False(admission.TryEnter());
    }
}
