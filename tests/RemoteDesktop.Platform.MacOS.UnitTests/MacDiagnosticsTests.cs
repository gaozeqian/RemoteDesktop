using RemoteDesktop.Platform.MacOS;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

public class MacDiagnosticsTests
{
    private const string MacOnly = "CoreGraphics diagnostics are macOS only.";

    /// <summary>
    /// Both CoreGraphics entry points are native calls with no injectable state, so what can be asserted here is that
    /// the P/Invoke surface can actually be read, and that coordinates stay finite when the cursor is available.
    /// A "bool is either true or false" assertion would prove nothing; the real behaviour of these diagnostics is
    /// exercised by the client's macOS self-check, which moves the pointer and compares positions.
    /// </summary>
    [Fact]
    public void Diagnostics_PInvoke_surface_can_be_read_without_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), MacOnly);

        var locationError = Record.Exception(() => { _ = MacDiagnostics.CurrentCursorLocation(); });
        var sleepError = Record.Exception(() => { _ = MacDiagnostics.IsDisplayAsleep(); });

        Assert.Null(locationError);
        Assert.Null(sleepError);

        if (MacDiagnostics.CurrentCursorLocation() is { } pt)
        {
            Assert.True(double.IsFinite(pt.X));
            Assert.True(double.IsFinite(pt.Y));
        }
    }
}
