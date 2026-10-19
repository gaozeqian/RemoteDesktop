using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Platform.Linux.Camera;
using RemoteDesktop.Platform.Linux.XdgPortal;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// The one part of the Linux camera path that can be tested without a controller: the explicit grant the portal needs
/// because a background host cannot be shown its access dialog.
/// </summary>
/// <remarks>
/// Run this from a systemd user unit named <c>app-RemoteDesktopHost.service</c> with
/// <c>RD_CAMERA_TEST=1 RD_CAMERA_APP_ID=RemoteDesktopHost</c>, a <c>RemoteDesktopHost.desktop</c> installed, and a
/// camera attached. The unit name is what the portal derives the app id from; starting it any other way tests an
/// app id that the portal will not use.
/// </remarks>
public sealed class CameraAccessTests
{
    private const string Gate = "RD_CAMERA_TEST";
    private const string ExpectedAppIdVariable = "RD_CAMERA_APP_ID";

    [Fact]
    public async Task The_explicit_grant_makes_the_camera_portal_answer_yes()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(Gate) == "1",
            "Needs a graphical session, a camera and the app-... systemd unit; set RD_CAMERA_TEST=1.");

        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var factory = services.GetRequiredService<ILoggerFactory>();
        var enumerator = new WaylandCameraEnumerator(factory, factory.CreateLogger<WaylandCameraEnumerator>());
        using var access = new CameraAccess(enumerator, factory, factory.CreateLogger<CameraAccess>());

        var appId = PortalAppId.FromCurrentProcess();
        Assert.NotNull(appId);
        Assert.Equal(Environment.GetEnvironmentVariable(ExpectedAppIdVariable), appId);

        var cancellationToken = TestContext.Current.CancellationToken;
        using var portal = new PortalConnection();
        try
        {
            await access.SetAllowedAsync(allowed: true, cancellationToken);
            Assert.Equal(
                CameraAccessState.Allowed,
                await access.GetStateAsync(cancellationToken));

            var cameras = enumerator.GetCameras();
            Assert.NotEmpty(cameras);
        }
        finally
        {
            // Remove the grant this test made, so a diagnostic run does not leave camera access behind. Deleting
            // returns the store to "not asked yet"; a pre-existing grant is not restored, which is why the test is
            // gated and documented rather than run in CI.
            await portal.DeleteCameraPermissionAsync(appId, cancellationToken);
        }
    }
}
