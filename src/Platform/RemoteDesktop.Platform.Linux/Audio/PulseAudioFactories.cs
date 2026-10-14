using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Audio;

namespace RemoteDesktop.Platform.Linux.Audio;

/// <summary>
/// Creates capture from the machine's own output.
/// </summary>
/// <remarks>
/// A new one each time, deliberately: the host creates a capture, and when the sequence ends it creates another.
/// That is how following the user's headphones works, so an instance that were reused would have to be told to
/// start over — and the caller already has a simpler way to say that.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PulseAudioCapturerFactory : IAudioCapturerFactory
{
    public IAudioCapturer Create() => new PulseAudioCapturer();
}

[SupportedOSPlatform("linux")]
public sealed class PulseAudioPlayerFactory : IAudioPlayerFactory
{
    public IAudioPlayer Create() => new PulseAudioPlayer();
}
