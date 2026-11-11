using RemoteDesktop.Platform.Linux.XdgPortal;
using Tmds.DBus.Protocol;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// How the portal connection fails, which is what every caller classifies: "there is no portal on this machine" has to
/// arrive in one shape whether the session bus address is missing, advertised but dead, or the portal itself refuses.
/// </summary>
public sealed class PortalConnectionTests
{
    private const string LinuxOnly = "The desktop portals, and D-Bus.";

    /// <summary>
    /// A machine whose bus address outlived its bus — what a crashed session leaves behind — must read as "no portals"
    /// rather than as a transport failure. It used to escape as <see cref="DBusConnectFailedException"/>, which no
    /// caller expects: the self-check crashed on it instead of reporting that the portals were unavailable.
    /// </summary>
    [Fact]
    public void A_session_bus_that_cannot_be_reached_is_reported_as_no_portal()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // An address that parses but names nothing, so the connection fails on the socket rather than on the syntax.
        // Given to the constructor rather than put in the environment: the D-Bus library resolves the session address
        // once per process, and a test that moved it would only work when it happened to run first.
        var error = Assert.Throws<PortalException>(() => new PortalConnection("unix:path=/nonexistent-rd-test/session-bus"));

        // The transport's own failure is kept as the cause: which bus, and why, are the two things a support report
        // needs from this message.
        Assert.IsAssignableFrom<DBusExceptionBase>(error.InnerException);
        Assert.Contains("/nonexistent-rd-test/session-bus", error.Message, StringComparison.Ordinal);
    }
}
