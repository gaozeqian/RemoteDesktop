using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// The application id the camera permission is keyed by. The rules are the portal's, not this product's, so the
/// cases below are the shapes systemd actually produces rather than shapes invented to make the regex look right.
/// </summary>
[Collection(DesktopEnvironmentGroup.Name)]
public sealed class PortalAppIdTests
{
    [Theory]
    [InlineData("app-RemoteDesktopHost.service", "RemoteDesktopHost")]
    [InlineData("app-RemoteDesktopHost@abc123.service", "RemoteDesktopHost")]
    // The portal reads this ambiguous form as launcher + "autostart"; mirror it rather than inventing a better
    // answer, because the permission must be written under the id the portal will actually look up.
    [InlineData("app-RemoteDesktopHost-autostart.service", "autostart")]
    [InlineData("app-gnome-remotedesktop-2437.scope", "remotedesktop")]
    [InlineData("app-org.gnome.Evolution\\x2dalarm\\x2dnotify-2437.scope", "org.gnome.Evolution-alarm-notify")]
    [InlineData("remotedesktop-host.service", null)]
    [InlineData("user@1000.service", null)]
    [InlineData("app-", null)]
    public void Unit_names_are_read_the_way_the_portal_reads_them(string unit, string? expected) =>
        Assert.Equal(expected, PortalAppId.ParseUnitName(unit));

    [Fact]
    public void A_unit_without_a_desktop_entry_is_not_an_app_id()
    {
        var home = Directory.CreateTempSubdirectory("rd-portal-app-id-").FullName;
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
            const string appId = "RemoteDesktopPortalTest";
            const string cgroup = "0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-RemoteDesktopPortalTest.service";

            // The unit name alone is not enough: the portal asks GDesktopAppInfo for the desktop file and treats a
            // missing one as "no app id". A grant written under an id the portal will not use would silently fail.
            Assert.Null(PortalAppId.FromCgroup(cgroup));

            Directory.CreateDirectory(Path.Combine(home, "applications"));
            File.WriteAllText(Path.Combine(home, "applications", appId + ".desktop"), "[Desktop Entry]\nType=Application\n");

            Assert.Equal(appId, PortalAppId.FromCgroup(cgroup));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
            Directory.Delete(home, recursive: true);
        }
    }
}
