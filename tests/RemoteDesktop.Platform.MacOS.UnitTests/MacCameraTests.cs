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
    public async Task Concurrent_enumerations_do_not_share_the_helpers_list_storage()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), MacOnly);

        // Regression: the helper owns one list per process and frees it on the next listing call. Two enumerators
        // reading that list at once crashed in Marshal.PtrToStringUTF8. Separate instances on purpose — the
        // process-wide buffer is what the lock has to protect, not one object's state.
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var enumerator = new MacCameraEnumerator();
            for (var i = 0; i < 25; i++)
            {
                Assert.NotNull(enumerator.GetCameras());
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        await Task.WhenAll(workers);
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
