using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Platform.Linux.Display;
using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// The portal decides which monitors exist in the stream; the compositor decides their size. A controller asks for a
/// monitor by name, and these are the rules for turning that request into one granted stream — the place where a
/// mixed-DPI desktop used to lose the screen entirely, and where a monitor that was never shared used to be served at
/// the wrong size.
/// </summary>
public sealed class PortalDisplayMatchTests
{
    /// <summary>A monitor as the compositor lists it: physical pixels, positioned by <c>round(logical × scale)</c>.</summary>
    private static DisplayInfo Monitor(string id, int logicalX, int logicalY, int physicalWidth, int physicalHeight, double scale = 1.0) =>
        new(
            id,
            id,
            new PixelRect(
                (int)Math.Round(logicalX * scale),
                (int)Math.Round(logicalY * scale),
                physicalWidth,
                physicalHeight),
            scale,
            IsPrimary: false);

    /// <summary>A monitor as the portal describes a granted stream: compositor units, not pixels.</summary>
    private static SharedScreen Granted(int logicalX, int logicalY, int logicalWidth, int logicalHeight) =>
        new(7, logicalX, logicalY, logicalWidth, logicalHeight);

    [Fact]
    public void The_shared_stream_is_the_screen_that_was_asked_for()
    {
        var left = Monitor("DP-1", 0, 0, 2560, 1440);
        var right = Monitor("HDMI-1", 2560, 0, 1920, 1080);

        var (_, actual) = PortalDisplayMatch.Resolve(
            right,
            [Granted(0, 0, 2560, 1440), Granted(2560, 0, 1920, 1080)],
            [left, right]);

        Assert.Equal("HDMI-1", actual.Id);
        Assert.Equal(new PixelRect(2560, 0, 1920, 1080), actual.Bounds);
    }

    /// <summary>
    /// At a fractional scale the two sides round differently: the monitor is 2560 device pixels wide, the portal says
    /// 1707 columns because that is what 2560 ÷ 1.5 is. A comparison without slack never finds it.
    /// </summary>
    [Fact]
    public void A_fractional_scale_does_not_hide_the_screen()
    {
        var hidpi = Monitor("eDP-1", 0, 0, 2560, 1440, scale: 1.5);

        var (_, actual) = PortalDisplayMatch.Resolve(hidpi, [Granted(0, 0, 1707, 960)], [hidpi]);

        Assert.Equal("eDP-1", actual.Id);
        Assert.Equal(2560, actual.Bounds.Width);
    }

    /// <summary>
    /// The dialog shares one monitor and the controller picks the other one. There is no second stream to give it, and
    /// capturing the shared monitor at the requested monitor's size produced frames with a stride the copier rejected.
    /// The shared screen's own size is the only one that works, and it is what gets announced.
    /// </summary>
    [Fact]
    public void One_granted_screen_is_captured_at_its_own_size_when_it_is_not_the_one_asked_for()
    {
        var left = Monitor("DP-1", 0, 0, 2560, 1440);
        var right = Monitor("HDMI-1", 2560, 0, 1920, 1080);

        var (_, actual) = PortalDisplayMatch.Resolve(right, [Granted(0, 0, 2560, 1440)], [left, right]);

        Assert.Equal("DP-1", actual.Id);
        Assert.Equal(new PixelRect(0, 0, 2560, 1440), actual.Bounds);
    }

    /// <summary>Identical monitors are told apart by where they sit; the first one in the list is not an answer.</summary>
    [Fact]
    public void Monitors_of_the_same_size_are_told_apart_by_position()
    {
        var left = Monitor("DP-1", 0, 0, 1920, 1080);
        var right = Monitor("DP-2", 1920, 0, 1920, 1080);

        var (_, actual) = PortalDisplayMatch.Resolve(left, [Granted(1920, 0, 1920, 1080)], [left, right]);

        Assert.Equal("DP-2", actual.Id);
    }

    /// <summary>When the compositor cannot name the shared screen, the scale in the request is all there is to go on.</summary>
    [Fact]
    public void A_stream_the_compositor_cannot_name_is_converted_with_the_requests_scale()
    {
        var requested = Monitor("HDMI-1", 2560, 0, 1920, 1080);

        var (_, actual) = PortalDisplayMatch.Resolve(requested, [Granted(0, 0, 2560, 1440)], []);

        Assert.Equal("HDMI-1", actual.Id);
        Assert.Equal(new PixelRect(0, 0, 2560, 1440), actual.Bounds);
    }

    /// <summary>
    /// Several monitors are shared and the requested one is not among them. Streaming one of the others under the
    /// requested name is what produced a picture nobody could place; the answer is to say what is shared instead.
    /// </summary>
    [Fact]
    public void A_screen_that_is_not_shared_is_refused_with_what_is_shared()
    {
        var left = Monitor("DP-1", 0, 0, 1920, 1080);
        var right = Monitor("DP-2", 1920, 0, 2560, 1440);
        var wanted = Monitor("HDMI-1", 4480, 0, 3840, 2160);

        var error = Assert.Throws<InvalidOperationException>(() =>
            PortalDisplayMatch.Resolve(
                wanted,
                [Granted(0, 0, 1920, 1080), Granted(1920, 0, 2560, 1440)],
                [left, right, wanted]));

        Assert.Contains("HDMI-1", error.Message, StringComparison.Ordinal);
        Assert.Contains("1920×1080", error.Message, StringComparison.Ordinal);
        Assert.Contains("2560×1440", error.Message, StringComparison.Ordinal);
    }
}
