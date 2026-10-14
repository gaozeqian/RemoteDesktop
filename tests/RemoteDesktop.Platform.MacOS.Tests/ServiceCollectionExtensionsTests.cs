using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.MacOS.Audio;
using RemoteDesktop.Platform.MacOS.Camera;
using RemoteDesktop.Platform.MacOS.Clipboard;
using RemoteDesktop.Platform.MacOS.Display;
using RemoteDesktop.Platform.MacOS.Input;
using RemoteDesktop.Platform.MacOS.Recording;
using RemoteDesktop.Platform.Posix;
using RemoteDesktop.Platform.Posix.Terminal;
using RemoteDesktop.Platform.MacOS.Video;

namespace RemoteDesktop.Platform.MacOS.Tests;

/// <summary>
/// Checks that <see cref="ServiceCollectionExtensions.AddMacOSPlatform"/> wires up every port the platform project
/// implements, with the implementation that was written for it.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void Every_port_that_has_an_implementation_is_registered()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS platform is macOS only.");

        var services = new ServiceCollection();
        services.AddMacOSPlatform(Path.Combine(Path.GetTempPath(), "remote-desktop-platform-tests"));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsType<KeychainSecretStore>(provider.GetRequiredService<ISecretStore>());
        Assert.IsType<CoreGraphicsDisplays>(provider.GetRequiredService<IDisplayEnumerator>());
        Assert.IsType<CGEventInputInjector>(provider.GetRequiredService<IInputInjector>());
        Assert.IsType<UnixUserContextGuard>(provider.GetRequiredService<IUserContextGuard>());
        Assert.IsType<PosixPtyHost>(provider.GetRequiredService<IPtyHost>());
        Assert.IsType<IOPMAssertionKeepAwake>(provider.GetRequiredService<ISystemKeepAwake>());
        Assert.IsType<MacClipboard>(provider.GetRequiredService<IClipboard>());
        Assert.IsType<MacCameraEnumerator>(provider.GetRequiredService<ICameraEnumerator>());
        Assert.IsType<MacCameraSourceFactory>(provider.GetRequiredService<ICameraSourceFactory>());
        Assert.IsType<MacAudioCapturerFactory>(provider.GetRequiredService<IAudioCapturerFactory>());
        Assert.IsType<MacAudioPlayerFactory>(provider.GetRequiredService<IAudioPlayerFactory>());

        // Two encoder factories, hardware first. The selector orders by IsHardware descending, so the order they
        // are registered in does not matter — but the pair must exist, or there is no fallback when the hardware
        // encoder refuses a configuration.
        var encoders = provider.GetServices<IVideoEncoderFactory>().ToList();
        Assert.Contains(encoders, factory => factory.IsHardware);
        Assert.Contains(encoders, factory => !factory.IsHardware);
        Assert.IsType<VideoToolboxH264DecoderFactory>(provider.GetRequiredService<IVideoDecoderFactory>());
        Assert.IsType<MacVideoRecorderFactory>(provider.GetRequiredService<IVideoRecorderFactory>());
        Assert.IsType<MacLoginItem>(provider.GetRequiredService<ILoginItem>());
    }

    [Fact]
    public void The_audio_and_camera_factories_produce_independent_objects()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS platform is macOS only.");

        var services = new ServiceCollection();
        services.AddMacOSPlatform(Path.Combine(Path.GetTempPath(), "remote-desktop-platform-tests"));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        // The factories are the singletons, not the ports they build: an audio player owns a device and a camera
        // source owns a capture session, so two live sessions each need their own. Sharing one would mean the second
        // session's Enqueue would be writing into the first one's queue.
        var players = provider.GetRequiredService<IAudioPlayerFactory>();
        var cameras = provider.GetRequiredService<ICameraSourceFactory>();

        Assert.NotSame(players.Create(), players.Create());

        // The camera half needs a camera to open, and a machine without one (a mini, a runner) cannot show it —
        // opening the id of a camera that is not there throws, which says nothing about independence. What such a
        // machine can still show is the refusal, so that is what is asserted instead.
        var first = FirstCameraOrNull(provider);
        if (first is null)
        {
            Assert.Throws<CameraUnavailableException>(() => cameras.Open("missing"));
            return;
        }

        Assert.NotSame(cameras.Open(first), cameras.Open(first));
    }

    private static string? FirstCameraOrNull(IServiceProvider provider)
    {
        var cameras = provider.GetRequiredService<ICameraEnumerator>().GetCameras();
        return cameras.Count > 0 ? cameras[0].Id : null;
    }
}
