using RemoteDesktop.Platform.Linux.Display;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// Reading the monitor layout from the compositor.
/// </summary>
/// <remarks>
/// The layout arrives as <c>ua((ssss)a(siiddada{sv})a{sv})a(iiduba(ssss)a{sv})a{sv}</c> — monitors with all their
/// modes, then a separate list of logical monitors saying where each one sits. Two things about that make it worth
/// testing against a real compositor rather than a fixture: the size of a monitor is in neither list (it is the mode
/// marked <c>is-current</c>), and the two lists are joined by connector name, so a reader that walks them out of step
/// returns a plausible-looking layout that is simply wrong. There is no way to notice that from an invented message.
/// </remarks>
public sealed class MutterDisplayConfigTests(ITestOutputHelper output)
{
    private const string LinuxOnly = "Mutter's display interface, which only exists on Linux.";
    private const string NeedsMutter = "This compositor does not implement Mutter's display interface.";

    [Fact]
    public void The_layout_is_read_without_anybody_being_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), NeedsMutter);

        using var config = new MutterDisplayConfig();
        var monitors = config.Read();

        Assert.NotEmpty(monitors);

        foreach (var monitor in monitors)
        {
            output.WriteLine(
                $"{monitor.Connector} '{monitor.DisplayName}' " +
                $"logical {monitor.Geometry.LogicalBounds} scale {monitor.Geometry.Scale} " +
                $"physical {monitor.Geometry.PhysicalBounds} primary={monitor.Geometry.IsPrimary}");

            Assert.False(string.IsNullOrEmpty(monitor.Connector));
            Assert.True(monitor.Geometry.PhysicalWidth > 0, $"{monitor.Connector} has no width.");
            Assert.True(monitor.Geometry.PhysicalHeight > 0, $"{monitor.Connector} has no height.");
        }

        // The compositor marks exactly one monitor primary. If none were marked, the controller would have nothing to
        // fall back to when the display it asked for is gone.
        Assert.Single(monitors, m => m.Geometry.IsPrimary);
    }

    [Fact]
    public void The_logical_size_is_the_physical_size_divided_by_the_scale()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), NeedsMutter);

        using var config = new MutterDisplayConfig();
        var monitors = config.Read();

        foreach (var monitor in monitors)
        {
            var geometry = monitor.Geometry;

            // This is the whole reason both coordinate systems are carried around: the compositor lays windows out in
            // scaled units, the encoder works in device pixels, and on a scaled screen they are different numbers.
            // Measured on this machine: a 2560x1440 mode at scale 1.33333 occupies 1920x1080 of the desktop.
            Assert.Equal(geometry.PhysicalWidth / geometry.Scale, geometry.LogicalWidth, 0.5);
            Assert.Equal(geometry.PhysicalHeight / geometry.Scale, geometry.LogicalHeight, 0.5);

            output.WriteLine(
                $"{monitor.Connector}: {geometry.PhysicalWidth}x{geometry.PhysicalHeight} at {geometry.Scale} " +
                $"-> {geometry.LogicalWidth}x{geometry.LogicalHeight}");
        }
    }

    [Fact]
    public void The_desktop_bounds_cover_every_monitor()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), NeedsMutter);

        using var config = new MutterDisplayConfig();
        var monitors = config.Read();

        var region = DisplayGeometry.LogicalUnion(monitors.Select(m => m.Geometry));

        foreach (var monitor in monitors)
        {
            var bounds = monitor.Geometry.LogicalBounds;
            Assert.True(bounds.X >= region.X, $"{monitor.Connector} starts left of the desktop.");
            Assert.True(bounds.Y >= region.Y, $"{monitor.Connector} starts above the desktop.");
            Assert.True(bounds.X + bounds.Width <= region.X + region.Width, $"{monitor.Connector} runs off the right.");
            Assert.True(bounds.Y + bounds.Height <= region.Y + region.Height, $"{monitor.Connector} runs off the bottom.");
        }
    }

    [Fact]
    public async Task Reading_it_repeatedly_gives_the_same_answer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), NeedsMutter);

        // The layout is read from the streaming loop every couple of seconds, so this call has to be repeatable and
        // has to be safe to make from a thread-pool thread. The second read is what proves the first did not leave
        // the connection in a state where the next one waits forever — which is how a blocked read loop behaves.
        using var config = new MutterDisplayConfig();
        var first = config.Read();
        var second = await Task.Run(config.Read);

        Assert.Equal(first, second);
    }
}
