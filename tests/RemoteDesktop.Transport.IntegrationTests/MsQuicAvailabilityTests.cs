using System.Net.Quic;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// Checks that MsQuic is actually loadable, which on macOS is a packaging property rather than a given.
/// </summary>
/// <remarks>
/// Windows gets msquic.dll from the .NET runtime pack. macOS gets nothing, so the application carries its own copy of
/// libmsquic and the OpenSSL it links against, staged by <c>native/macos/fetch-libmsquic.sh</c> and copied next to the
/// executable by the transport project. A failure here means those files are missing or unloadable — the runtime
/// reports it only as <c>PlatformNotSupportedException: Unable to load MsQuic library version '2'</c>, which says
/// nothing about where to look.
/// </remarks>
public class MsQuicAvailabilityTests
{
    [Fact]
    public void The_native_MsQuic_library_can_be_loaded()
    {
        Assert.True(
            QuicListener.IsSupported,
            "libmsquic could not be loaded. On macOS run native/macos/fetch-libmsquic.sh and rebuild.");
        Assert.True(QuicConnection.IsSupported, "libmsquic loaded but reports no connection support.");
    }
}
