using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Platform;

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
    /// Still to come, in the order the port plan schedules them:
    /// <c>ISystemKeepAwake</c> (logind, with the transport bridge),
    /// <c>IDisplayEnumerator</c> / <c>IScreenCapturerFactory</c> (portal + PipeWire),
    /// <c>IInputInjector</c> (EIS over the same portal session),
    /// <c>IVideoEncoderFactory</c> ×3 and <c>IVideoDecoderFactory</c> (libavcodec),
    /// <c>IClipboard</c>, <c>IPtyHost</c>, <c>ICameraEnumerator</c> / <c>ICameraSourceFactory</c>,
    /// <c>IAudioCapturerFactory</c> / <c>IAudioPlayerFactory</c>, and <c>IVideoRecorderFactory</c>.
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
        services.AddSingleton<IUserContextGuard, LinuxUserContextGuard>();
        return services;
    }
}
