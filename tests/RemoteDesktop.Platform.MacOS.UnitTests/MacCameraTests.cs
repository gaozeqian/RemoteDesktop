using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Platform.MacOS.Camera;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

public class MacCameraTests
{
    private const string MacOnly = "AVFoundation camera is macOS only.";

    [Fact]
    public void MacCameraEnumerator_GetCameras_returns_clean_list_without_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), MacOnly);

        var enumerator = new MacCameraEnumerator();
        var cameras = enumerator.GetCameras();

        Assert.NotNull(cameras);
        foreach (var camera in cameras)
        {
            Assert.False(string.IsNullOrWhiteSpace(camera.Id));
            Assert.False(string.IsNullOrWhiteSpace(camera.Name));
        }
    }

    [Fact]
    public void MacCameraSourceFactory_Open_missing_camera_throws_CameraUnavailableException()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), MacOnly);

        var enumerator = new MacCameraEnumerator();
        var factory = new MacCameraSourceFactory(enumerator);
        Assert.Throws<CameraUnavailableException>(() => factory.Open("non-existent-camera-guid-12345"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MacCameraSourceFactory_Open_empty_camera_id_throws_ArgumentException(string invalidId)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), MacOnly);

        var enumerator = new MacCameraEnumerator();
        var factory = new MacCameraSourceFactory(enumerator);
        Assert.Throws<ArgumentException>(() => factory.Open(invalidId));
    }
}
