using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.Linux.Audio;
using RemoteDesktop.Platform.Linux.Display;
using RemoteDesktop.Platform.Linux.Video;
using RemoteDesktop.Platform.Posix;
using RemoteDesktop.Platform.Posix.Terminal;

namespace RemoteDesktop.Platform.Linux;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Linux implementations of the platform ports. The Windows registration is the mirror of this list.
    /// </summary>
    /// <param name="dataDirectory">Where state is kept; <c>~/.local/share/RemoteDesktop</c> for both the client and the
    /// controlled end, since on this platform they run as the same user.</param>
    /// <remarks>
    /// <para>
    /// Ports are registered as they are built rather than stubbed out up front. Each one has to be a real
    /// implementation before anything can be said about it, and a stub that throws from its constructor would only
    /// move a failure from "this feature does not work yet" to "the host does not start".
    /// </para>
    /// <para>
    /// Ports shared with any other Posix system — the secret store, the user-context guard and the terminal — come
    /// from RemoteDesktop.Posix rather than from here, so that a second Posix platform would not mean a second copy
    /// of them.
    ///
    /// Still to come, in the order the port plan schedules them:
    /// <c>IInputInjector</c> (EIS over the same portal session),
    /// <c>IClipboard</c>, and <c>ICameraEnumerator</c> / <c>ICameraSourceFactory</c>.
    /// </para>
    /// <para>
    /// <c>IAgentGateway</c> is absent on purpose and will stay absent. On Windows the privileged service cannot see the
    /// interactive desktop, so it supervises an agent process that can; here the equivalent hazard does not exist, the
    /// host runs as the signed-in user inside that user's graphical session, and the feature modules are registered
    /// directly. <c>LocalHostControl</c> already accepts a null gateway, so nothing above this layer has to know.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("linux")]
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services, string dataDirectory)
    {
        services.AddSingleton<ISecretStore>(new FileSecretStore(Path.Combine(dataDirectory, "secrets")));
        services.AddSingleton<IUserContextGuard, UnixUserContextGuard>();
        services.AddSingleton<ISystemKeepAwake, LogindKeepAwake>();
        services.AddSingleton<IDisplayEnumerator, WaylandDisplayEnumerator>();
        services.AddSingleton<IScreenCapturerFactory, PipeWireScreenCapturerFactory>();
        services.AddSingleton<IVideoDecoderFactory, FfmpegH264DecoderFactory>();
        services.AddSingleton<IPtyHost, PosixPtyHost>();
        services.AddSingleton<IAudioCapturerFactory, PulseAudioCapturerFactory>();
        services.AddSingleton<IAudioPlayerFactory, PulseAudioPlayerFactory>();
        services.AddSingleton<IVideoRecorderFactory, FfmpegVideoRecorderFactory>();

        // Three encoders, mixed together by VideoCodecSelector: hardware first, then by priority, with every failure
        // falling through to the next. The software one is registered last because it is the one that always works.
        services.AddSingleton<IVideoEncoderFactory, NvencH264EncoderFactory>();
        services.AddSingleton<IVideoEncoderFactory, VaapiH264EncoderFactory>();
        services.AddSingleton<IVideoEncoderFactory, SoftwareH264EncoderFactory>();
        return services;
    }
}
