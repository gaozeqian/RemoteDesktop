using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.Windows.Display;
using RemoteDesktop.Platform.Windows.Input;
using RemoteDesktop.Platform.Windows.MediaFoundation;

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
        services.AddSingleton<IVideoEncoderFactory>(new MediaFoundationH264EncoderFactory(hardware: true));
        services.AddSingleton<IVideoEncoderFactory>(new MediaFoundationH264EncoderFactory(hardware: false));
        services.AddSingleton<IVideoDecoderFactory, MediaFoundationH264DecoderFactory>();
        return services;
    }
}
