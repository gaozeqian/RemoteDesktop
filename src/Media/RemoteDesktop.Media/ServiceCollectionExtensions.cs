using Microsoft.Extensions.DependencyInjection;

namespace RemoteDesktop.Media;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers codec selection. Software (FFmpeg/openh264) and Opus backends are added in milestone M2.</summary>
    public static IServiceCollection AddRemoteDesktopMedia(this IServiceCollection services)
    {
        services.AddSingleton<VideoCodecSelector>();
        return services;
    }
}
