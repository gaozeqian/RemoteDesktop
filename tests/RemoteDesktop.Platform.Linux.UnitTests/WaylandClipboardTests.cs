using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Platform.Linux.Clipboard;
using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux.UnitTests;

public class WaylandClipboardTests
{
    private const string LinuxOnly = "Wayland clipboard is Linux only.";

    [Fact]
    public void Get_returns_null_when_portal_control_session_is_not_ready()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var session = new PortalControlSession(NullLoggerFactory.Instance, NullLogger<PortalControlSession>.Instance);
        using var clipboard = new WaylandClipboard(session, NullLogger<WaylandClipboard>.Instance);

        Assert.Null(clipboard.Get());
    }

    [Fact]
    public void Set_throws_ArgumentNullException_when_content_is_null()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var session = new PortalControlSession(NullLoggerFactory.Instance, NullLogger<PortalControlSession>.Instance);
        using var clipboard = new WaylandClipboard(session, NullLogger<WaylandClipboard>.Instance);

        Assert.Throws<ArgumentNullException>(() => clipboard.Set(null!));
    }

    [Fact]
    public void Set_returns_safely_when_portal_control_session_is_not_ready()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var session = new PortalControlSession(NullLoggerFactory.Instance, NullLogger<PortalControlSession>.Instance);
        using var clipboard = new WaylandClipboard(session, NullLogger<WaylandClipboard>.Instance);

        // Does not throw when the portal cannot be reached; logs unavailable and returns.
        clipboard.Set(new TextClipboardContent("test content"));
    }

    [Fact]
    public void Changed_event_handler_can_be_added_and_removed_cleanly()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var session = new PortalControlSession(NullLoggerFactory.Instance, NullLogger<PortalControlSession>.Instance);
        using var clipboard = new WaylandClipboard(session, NullLogger<WaylandClipboard>.Instance);

        EventHandler handler = (_, _) => { };
        clipboard.Changed += handler;
        clipboard.Changed -= handler;
    }

    [Fact]
    public void Dispose_can_be_called_multiple_times_without_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        using var session = new PortalControlSession(NullLoggerFactory.Instance, NullLogger<PortalControlSession>.Instance);
        var clipboard = new WaylandClipboard(session, NullLogger<WaylandClipboard>.Instance);

        clipboard.Dispose();
        clipboard.Dispose();
    }
}
