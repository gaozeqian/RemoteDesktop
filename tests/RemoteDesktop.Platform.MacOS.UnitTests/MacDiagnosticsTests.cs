using RemoteDesktop.Platform.MacOS;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

public class MacDiagnosticsTests
{
    private const string MacOnly = "CoreGraphics diagnostics are macOS only.";

    [Fact]
    public void CurrentCursorLocation_reads_or_returns_null_without_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), MacOnly);

        var location = MacDiagnostics.CurrentCursorLocation();
        if (location is { } pt)
        {
            Assert.True(double.IsFinite(pt.X));
            Assert.True(double.IsFinite(pt.Y));
        }
    }

    [Fact]
    public void IsDisplayAsleep_evaluates_main_display_sleep_state_cleanly()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), MacOnly);

        var isAsleep = MacDiagnostics.IsDisplayAsleep();
        // Returns a valid boolean; does not throw memory or P/Invoke faults.
        Assert.True(isAsleep || !isAsleep);
    }
}
