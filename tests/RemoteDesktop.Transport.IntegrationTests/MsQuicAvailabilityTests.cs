using System.Net.Quic;
using System.Runtime.InteropServices;

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
        // The message carries what the loader saw, because on macOS this is a packaging property and the runtime
        // reports a miss the same way for every cause: a missing file, a file in the wrong place, and a file that
        // fails to load all read as IsSupported == false.
        var staged = Path.Combine(AppContext.BaseDirectory, "libmsquic.dylib");
        var diagnostics = string.Join(
            Environment.NewLine + "  ",
            $"platform: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})",
            $"library next to the test host: {File.Exists(staged)} ({AppContext.BaseDirectory})",
            $"DYLD_LIBRARY_PATH: {(Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") is { Length: > 0 } path ? path : "(unset)")}",
            $"NATIVE_DLL_SEARCH_DIRECTORIES: {(Environment.GetEnvironmentVariable("NATIVE_DLL_SEARCH_DIRECTORIES") is { Length: > 0 } directories ? directories : "(unset)")}",
            $"NativeLibrary.TryLoad('libmsquic'): {NativeLibrary.TryLoad("libmsquic", out _)}");

        Assert.True(
            QuicListener.IsSupported,
            $"libmsquic could not be loaded. On macOS run native/macos/fetch-libmsquic.sh and rebuild.{Environment.NewLine}  {diagnostics}");
        Assert.True(QuicConnection.IsSupported, "libmsquic loaded but reports no connection support.");
    }
}
