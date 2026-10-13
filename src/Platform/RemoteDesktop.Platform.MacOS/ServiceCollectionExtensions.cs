using System.Runtime.Versioning;
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
using RemoteDesktop.Platform.MacOS.Terminal;
using RemoteDesktop.Platform.MacOS.Video;

namespace RemoteDesktop.Platform.MacOS;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the macOS implementations of the platform ports: Keychain secrets, CoreGraphics display enumeration
    /// and event injection, ScreenCaptureKit capture and loopback audio, the pasteboard, AVFoundation cameras,
    /// AudioQueue playback, VideoToolbox H.264, the BSD pseudo-terminal, and the IOKit power assertion.
    /// </summary>
    /// <remarks>
    /// There is no scope argument the way Windows has one — DPAPI has a machine-or-user choice to make, the keychain
    /// is per-user by construction. The ports that are missing are the ones with no macOS implementation yet
    /// (recording); each arrives with the milestone that provides it, and until then the container refuses
    /// to build a feature that needs it, which is the behaviour we want.
    /// </remarks>
    /// <param name="dataDirectory">
    /// Where protected state lives. It also scopes the keychain items, so two data directories are two devices — the
    /// same rule the Windows registration follows.
    /// </param>
    [SupportedOSPlatform("macos")]
    public static IServiceCollection AddMacOSPlatform(this IServiceCollection services, string dataDirectory)
    {
        services.AddSingleton<ISecretStore>(new KeychainSecretStore(dataDirectory));
        services.AddSingleton<IDisplayEnumerator, CoreGraphicsDisplays>();
        services.AddSingleton<IScreenCapturerFactory, ScreenCaptureKitCapturerFactory>();
        services.AddSingleton<IInputInjector, CGEventInputInjector>();
        services.AddSingleton<IUserContextGuard, MacUserContextGuard>();
        services.AddSingleton<IPtyHost, PosixPtyHost>();
        services.AddSingleton<ISystemKeepAwake, IOPMAssertionKeepAwake>();

        // The enumerator is a singleton because the source factory reads it on every Open, and rebuilding the
        // AVFoundation device list each time would be work for nothing. The Windows registration has the same shape.
        services.AddSingleton<ICameraEnumerator, MacCameraEnumerator>();
        services.AddSingleton<ICameraSourceFactory, MacCameraSourceFactory>();

        // A factory rather than an instance so the container owns the object and disposes it on shutdown, which is
        // what stops the pasteboard polling. The Windows registration does the same for the same reason.
        services.AddSingleton<IClipboard>(_ => new MacClipboard());

        // Audio capture is loopback through ScreenCaptureKit, so it rides on the same Screen Recording grant the
        // display capture already needs rather than introducing a permission of its own.
        services.AddSingleton<IAudioCapturerFactory, MacAudioCapturerFactory>();
        services.AddSingleton<IAudioPlayerFactory, MacAudioPlayerFactory>();

        // Two H.264 factories, hardware first with software behind it, and one decoder. That is the whole of the
        // VideoToolbox codec surface: H.265 exists on this machine but the wire format is H.264, so an encoder for
        // it would be code nothing can talk to. HEVC is a decision to take deliberately, not a default to inherit.
        services.AddSingleton<IVideoEncoderFactory>(new VideoToolboxH264EncoderFactory(hardware: true));
        services.AddSingleton<IVideoEncoderFactory>(new VideoToolboxH264EncoderFactory(hardware: false));
        services.AddSingleton<IVideoDecoderFactory, VideoToolboxH264DecoderFactory>();
        return services;
    }
}
