using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Media;

namespace RemoteDesktop.Platform.MacOS.Recording;

/// <summary>
/// Builds the macOS video recorder, which encodes BGRA frames to an H.264 MP4 through AVAssetWriter.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacVideoRecorderFactory : IVideoRecorderFactory
{
    public IVideoRecorder Create() => new MacVideoRecorder();
}
