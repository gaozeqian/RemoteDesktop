using Microsoft.Extensions.DependencyInjection;

namespace RemoteDesktop.Media;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers codec selection. The H.264 encoders and decoders come from the platform layers; Opus audio lives in this project (OpusAudio).</summary>
    public static IServiceCollection AddRemoteDesktopMedia(this IServiceCollection services)
    {
        services.AddSingleton<VideoCodecSelector>();
        return services;
    }
}
