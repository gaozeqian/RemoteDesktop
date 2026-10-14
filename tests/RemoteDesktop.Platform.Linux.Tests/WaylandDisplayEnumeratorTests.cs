using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Platform.Linux.Display;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// Turning the compositor's monitors into the screens the rest of the product talks about.
/// </summary>
/// <remarks>
/// The interesting part is which of the two coordinate systems ends up in <c>Bounds</c>. The interface says virtual
/// desktop pixels, everything downstream (capture, encoding, the pointer on the wire) takes it at its word, and the
/// compositor's own units are scaled — so putting the logical rectangle there would work on every screen at scale 1
/// and be wrong on every screen that is not. Measured on this machine: a 2560x1440 mode at scale 1.33333, which
/// occupies 1920x1080 of the desktop.
/// </remarks>
public sealed class WaylandDisplayEnumeratorTests
{
    private const string LinuxOnly = "Mutter's display interface, which only exists on Linux.";
    private const string NeedsMutter = "This compositor does not implement Mutter's display interface.";

    private static WaylandDisplayEnumerator Create() => new(NullLogger<WaylandDisplayEnumerator>.Instance);

    [Fact]
    public void The_screens_come_back_named_by_their_connector()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), NeedsMutter);

        using var enumerator = Create();
        using var config = new MutterDisplayConfig();

        var displays = enumerator.GetDisplays();
        var monitors = config.Read();

        Assert.NotEmpty(displays);

        // The controller asks for a screen by this name and holds on to it across resolution changes, so it has to be
        // the connector rather than anything derived from the current mode.
        Assert.Equal(monitors.Select(m => m.Connector), displays.Select(d => d.Id));
    }

    [Fact]
    public void The_bounds_are_device_pixels_and_the_scale_is_carried_beside_them()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), NeedsMutter);

        using var enumerator = Create();
        using var config = new MutterDisplayConfig();

        var displays = enumerator.GetDisplays().ToDictionary(d => d.Id, StringComparer.Ordinal);
        var monitors = config.Read();

        foreach (var monitor in monitors)
        {
            var display = displays[monitor.Connector];

            Assert.Equal(monitor.Geometry.PhysicalBounds, display.Bounds);
            Assert.Equal(monitor.Geometry.Scale, display.Scale, 6);

            // Handing out the compositor's own rectangle instead is the mistake that hides on a screen at scale 1 and
            // is wrong on every other one, so where the two differ the difference is asserted rather than assumed.
            if (Math.Abs(monitor.Geometry.Scale - 1.0) > 0.0001)
            {
                Assert.NotEqual(monitor.Geometry.LogicalWidth, display.Bounds.Width);
            }
        }
    }

    [Fact]
    public void Exactly_one_screen_is_primary_so_there_is_something_to_fall_back_to()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), NeedsMutter);

        using var enumerator = Create();
        var displays = enumerator.GetDisplays();

        // The channel picks the primary screen when the controller asks for one that has gone away. With none marked,
        // that request has nothing to fall back to and the session ends instead of moving to another screen.
        Assert.Single(displays, d => d.IsPrimary);
    }
}
