using RemoteDesktop.Domain;

namespace RemoteDesktop.Core.UnitTests;

public class AccessGrantTests
{
    [Fact]
    public void Effective_permissions_are_intersection_of_requested_and_allowed()
    {
        var grant = AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.ViewScreen | Permissions.ControlInput);

        Assert.Equal(Permissions.ViewScreen | Permissions.ControlInput, grant.Permissions);
    }

    [Fact]
    public void Watch_mode_strips_everything_that_changes_the_host_even_if_allowed()
    {
        var grant = AccessGrant.Create(SessionMode.View, Permissions.All, Permissions.All);

        Assert.Equal(Permissions.ViewOnly, grant.Permissions);
        Assert.False(grant.Allows(Permissions.ControlInput));
        Assert.False(grant.Allows(Permissions.FileTransfer));
        Assert.False(grant.Allows(Permissions.Terminal));
        Assert.False(grant.Allows(Permissions.Clipboard));
        Assert.False(grant.Allows(Permissions.Camera));
    }

    [Fact]
    public void Input_requires_screen_access()
    {
        var grant = AccessGrant.Create(SessionMode.Control, Permissions.ControlInput | Permissions.FileTransfer, Permissions.All);

        Assert.False(grant.Allows(Permissions.ControlInput));
        Assert.True(grant.Allows(Permissions.FileTransfer));
    }

    [Fact]
    public void Downgrade_to_view_removes_control()
    {
        var grant = AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All).DowngradeToView();

        Assert.Equal(SessionMode.View, grant.Mode);
        Assert.Equal(Permissions.ViewOnly, grant.Permissions);
    }

    [Fact]
    public void Allows_none_is_false() =>
        Assert.False(AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All).Allows(Permissions.None));
}
