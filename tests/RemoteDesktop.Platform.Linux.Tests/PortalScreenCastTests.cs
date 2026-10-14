using RemoteDesktop.Platform.Linux.XdgPortal;
using RemoteDesktop.Platform.Posix;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// Asking the desktop for a screen to share, over the portals.
/// </summary>
/// <remarks>
/// <para>
/// These are the only tests in the suite that need a person. Sharing a screen requires consent, and consent means a
/// dialog with a Share button in it, so the tests are skipped unless <c>RD_PORTAL_TEST=1</c> is set — a suite that
/// stops on a modal dialog would be worse than no suite.
/// </para>
/// <para>
/// The first run with that variable set asks once. The grant is then written to a restore token under the
/// application's own data directory, and the portal honours it on later runs, so the prompt does not come back. That
/// persistence is not a convenience for the tests — it is the thing that makes unattended access possible at all,
/// and running these tests is how it gets checked.
/// </para>
/// <para>
/// What they verify that nothing else can: that the reply to <c>Start</c> really does unpack into screens with
/// usable geometry. The reply is <c>a(ua{sv})</c> — an array of structs of a number and a dictionary of variants —
/// and it is read through the variant rather than through the message's reader, which is the kind of thing that
/// silently returns zero items when it is wrong.
/// </para>
/// </remarks>
public sealed class PortalScreenCastTests(ITestOutputHelper output)
{
    private const string LinuxOnly = "The desktop portals, which only exist on Linux.";
    private const string NeedsConsent = "Needs a person to click Share; set RD_PORTAL_TEST=1 to run it.";

    private const string RestoreTokenName = "portal-restore-token";

    /// <summary>
    /// Where the restore token is kept between runs.
    /// </summary>
    /// <remarks>
    /// Deliberately a fixed directory under the application's own data folder rather than a temporary one. A token
    /// thrown away after each run would mean a dialog every time, and the point of storing it is that it survives.
    /// </remarks>
    private static string TokenDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteDesktop",
            "portal-test");

    private static bool Enabled => Environment.GetEnvironmentVariable("RD_PORTAL_TEST") == "1";

    [Fact]
    public async Task The_compositor_offers_the_cursor_mode_the_client_needs()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipWhen(!Enabled, NeedsConsent);

        using var portal = new PortalConnection();
        var (sources, cursors, version) = await portal.ScreenCastCapabilitiesAsync(TestContext.Current.CancellationToken);

        output.WriteLine($"ScreenCast version {version}, sources {sources}, cursor modes {cursors}");

        // A whole screen, rather than one window: the controller shows a desktop, not an application.
        Assert.True((sources & PortalConnection.SourceMonitor) != 0, "This compositor cannot share a whole screen.");

        // The one that matters. Without it the pointer is painted into the picture, and the controller draws the
        // pointer it was told about as well — two cursors, one of them a frame behind.
        Assert.True(
            (cursors & PortalConnection.CursorMetadata) != 0,
            $"This compositor cannot report the pointer separately (it offers {cursors}).");
    }

    [Fact]
    public async Task The_screens_the_portal_returns_are_the_screens_that_were_asked_for()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipWhen(!Enabled, NeedsConsent);

        var cancellationToken = TestContext.Current.CancellationToken;
        var seat = new FileSecretStore(TokenDirectory);
        var restoreToken = seat.Load(RestoreTokenName) is { Length: > 0 } stored
            ? System.Text.Encoding.UTF8.GetString(stored)
            : null;

        if (restoreToken is null)
        {
            output.WriteLine(">>> No stored grant yet — a dialog will ask for permission. Choose a screen and share it. <<<");
        }

        using var portal = new PortalConnection();

        var (session, streams) = await portal.OpenScreenCastAsync(
            restoreToken,
            fresh => seat.Save(RestoreTokenName, System.Text.Encoding.UTF8.GetBytes(fresh)),
            cancellationToken);

        output.WriteLine($"session {session}");

        Assert.NotEmpty(streams);

        foreach (var screen in streams)
        {
            output.WriteLine($"  node {screen.NodeId} at ({screen.X},{screen.Y}) {screen.Width}x{screen.Height}");

            // A screen with no size is a stream that was described but never configured, and a picture read from it
            // would be the wrong shape or empty.
            Assert.NotEqual(0u, screen.NodeId);
            Assert.True(screen.Width > 0, "A shared screen came back with no width.");
            Assert.True(screen.Height > 0, "A shared screen came back with no height.");
        }

        // The descriptor the pictures are read from. Held only long enough to prove it arrives; PipeWire is not
        // spoken to here, because that is the capturer's job and it has its own tests.
        using var remote = await portal.OpenPipeWireRemoteAsync(session, cancellationToken);
        Assert.False(remote.IsInvalid, "The portal did not hand over a PipeWire descriptor.");
        Assert.True(remote.IsClosed is false);
    }
}
