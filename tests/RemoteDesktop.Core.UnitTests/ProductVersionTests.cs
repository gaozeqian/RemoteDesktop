using System.Diagnostics;
using ClientVersion = RemoteDesktop.Application.ProductVersion;
using ServerVersion = RemoteDesktop.Server.Shared.ProductVersion;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// One number, two shapes: what people and package names see, and what diagnostics carry.
/// </summary>
public class ProductVersionTests
{
    [Fact]
    public void Display_is_three_parts_and_Full_starts_with_it()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", ClientVersion.Display);
        Assert.StartsWith(ClientVersion.Display, ClientVersion.Full, StringComparison.Ordinal);
        Assert.DoesNotContain('+', ClientVersion.Display);
    }

    [Fact]
    public void The_client_and_the_server_report_the_same_version()
    {
        // The copies exist only because the two sides may not depend on each other's layer. They read the same
        // Directory.Build.props, and this is what keeps them from becoming two answers.
        Assert.Equal(ClientVersion.Display, ServerVersion.Display);
        Assert.Equal(ClientVersion.Full, ServerVersion.Full);
    }

    [Fact]
    public void Full_carries_the_commit_that_the_file_metadata_does_not()
    {
        Assert.SkipWhen(ClientVersion.Full == ClientVersion.Display, "This build has no source revision metadata.");

        // Telemetry needs the commit to tell two builds of the same release apart.
        Assert.StartsWith(ClientVersion.Display + "+", ClientVersion.Full, StringComparison.Ordinal);
        Assert.Matches(@"^\d+\.\d+\.\d+\+[0-9a-f]+$", ClientVersion.Full);

        // The file people see must not: it is the three-part product version.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PE version resources are Windows-only.");
        var metadata = FileVersionInfo.GetVersionInfo(typeof(ClientVersion).Assembly.Location);
        Assert.Equal(ClientVersion.Display, metadata.ProductVersion);
        Assert.Equal($"{ClientVersion.Display}.0", metadata.FileVersion);
    }
}
