using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.Windows.Agent;
using RemoteDesktop.Platform.Windows.Audio;
using RemoteDesktop.Platform.Windows.Camera;
using RemoteDesktop.Platform.Windows.Clipboard;
using RemoteDesktop.Platform.Windows.Display;
using RemoteDesktop.Platform.Windows.Input;
using RemoteDesktop.Platform.Windows.MediaFoundation;
using RemoteDesktop.Platform.Windows.Terminal;

namespace RemoteDesktop.Platform.Windows;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Windows implementations of the platform ports: DPAPI secrets, DXGI capture, SendInput and
    /// Media Foundation H.264 (hardware encoders first, the built-in software encoder as fallback).
    /// ConPTY, WASAPI and camera adapters are added in later milestones.
    /// </summary>
    /// <param name="dataDirectory">Where protected state is stored (%ProgramData% for the service, %AppData% for the UI).</param>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services, string dataDirectory, DataProtectionScope scope)
    {
        services.AddSingleton<ISecretStore>(new DpapiSecretStore(Path.Combine(dataDirectory, "secrets"), scope));
        services.AddSingleton<IDisplayEnumerator, DxgiDisplayEnumerator>();
        services.AddSingleton<IScreenCapturerFactory, DxgiScreenCapturerFactory>();
        services.AddSingleton<IInputInjector, SendInputInjector>();
        services.AddSingleton<IUserContextGuard, WindowsUserContextGuard>();
        services.AddSingleton<IPtyHost, ConPtyHost>();
        services.AddSingleton<ICameraEnumerator, MediaFoundationCameraEnumerator>();
        services.AddSingleton<ICameraSourceFactory, MediaFoundationCameraSourceFactory>();
        services.AddSingleton<IClipboard>(_ => new WindowsClipboard());
        services.AddSingleton<IAudioCapturerFactory, WasapiAudioCapturerFactory>();
        services.AddSingleton<IAudioPlayerFactory, WasapiAudioPlayerFactory>();
        services.AddSingleton<IVideoEncoderFactory>(new MediaFoundationH264EncoderFactory(hardware: true));
        services.AddSingleton<IVideoEncoderFactory>(new MediaFoundationH264EncoderFactory(hardware: false));
        services.AddSingleton<IVideoDecoderFactory, MediaFoundationH264DecoderFactory>();
        return services;
    }

    /// <summary>
    /// Host service side of the agent: supervises the session agent and exposes it as an <see cref="IAgentGateway"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddWindowsAgentGateway(this IServiceCollection services, AgentGatewayOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<WindowsAgentGateway>();
        services.AddSingleton<IAgentGateway>(sp => sp.GetRequiredService<WindowsAgentGateway>());
        services.AddHostedService(sp => sp.GetRequiredService<WindowsAgentGateway>());
        return services;
    }
}
