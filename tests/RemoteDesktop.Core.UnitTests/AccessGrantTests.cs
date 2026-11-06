using RemoteDesktop.Application.Features;
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

    /// <summary>Taking the screen away leaves nothing to type into or copy from, however the grant got there.</summary>
    [Fact]
    public void Revoking_the_screen_also_removes_input_and_clipboard()
    {
        var grant = AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All).Revoke(Permissions.ViewScreen);

        Assert.False(grant.Allows(Permissions.ViewScreen));
        Assert.False(grant.Allows(Permissions.ControlInput));
        Assert.False(grant.Allows(Permissions.Clipboard));
        Assert.True(grant.Allows(Permissions.FileTransfer));
    }

    /// <summary>
    /// The host downgrades a session and the local user revokes the camera from two threads at once. Each reads the
    /// grant, derives a new one and writes it back, so without an atomic update the second write silently undoes the
    /// first.
    /// </summary>
    [Fact]
    public async Task Concurrent_downgrade_and_revoke_both_take_effect()
    {
        for (var attempt = 0; attempt < 20_000; attempt++)
        {
            var context = new SessionContext(
                SessionId.New(),
                SessionRole.Host,
                null!,
                AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));
            using var gate = new Barrier(2);

            var downgrade = Task.Run(() =>
            {
                gate.SignalAndWait();
                context.UpdateGrant(grant => grant.DowngradeToView());
            }, TestContext.Current.CancellationToken);
            var revoke = Task.Run(() =>
            {
                gate.SignalAndWait();
                context.UpdateGrant(grant => grant.Revoke(Permissions.Camera));
            }, TestContext.Current.CancellationToken);
            await Task.WhenAll(downgrade, revoke);

            Assert.Equal(SessionMode.View, context.Grant.Mode);
            Assert.False(context.Grant.Allows(Permissions.Camera));
        }
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
